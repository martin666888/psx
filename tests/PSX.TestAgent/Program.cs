using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
await new FakeAcpAgent().RunAsync();

internal sealed class FakeAcpAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _outputLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _clientResponses = new();
    private readonly ConcurrentDictionary<string, JsonElement> _pendingPrompts = new();
    private bool _clientSupportsBooleanConfigOptions;
    private int _nextClientRequestId = 7000;

    // Standard ACP terminal-auth simulation. Scenario format is
    // "auth:<method>:<mode>" where method is new|load|prompt and mode is
    // recover|blocked. "recover" fails the gated method with -32000 until an
    // authenticate call succeeds; "blocked" fails both the gated method and
    // authenticate forever (the client must stay in auth_required).
    private readonly string _authGate;
    private readonly string _authMode;
    private bool _authenticated;

    // When true the agent silently ignores session/cancel so the client's cancel
    // path must fall back to force-resetting the transport after its grace.
    private readonly bool _ignoreCancel;

    // "resume" advertises sessionCapabilities.resume and serves session/resume
    // (session/load then fails loudly so tests prove which path ran).
    // "resume:notfound" advertises the capability but answers session/resume
    // with -32601 so the client must fall back to session/load exactly once.
    private readonly bool _advertiseResume;
    private readonly bool _resumeMethodMissing;

    public FakeAcpAgent()
    {
        var scenario = Environment.GetEnvironmentVariable("PSX_TEST_AGENT_SCENARIO") ?? "default";
        _ignoreCancel = string.Equals(scenario, "ignorecancel", StringComparison.OrdinalIgnoreCase);
        _advertiseResume = scenario is "resume" or "resume:notfound";
        _resumeMethodMissing = scenario == "resume:notfound";
        var parts = scenario.Split(':');
        if (parts.Length == 3 && parts[0] == "auth")
        {
            _authGate = parts[1];
            _authMode = parts[2];
            // Gating session/resume only makes sense when it is advertised.
            if (_authGate == "resume")
                _advertiseResume = true;
        }
        else
        {
            _authGate = "";
            _authMode = "";
        }
    }

    public async Task RunAsync()
    {
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonElement message;
            try
            {
                using var document = JsonDocument.Parse(line);
                message = document.RootElement.Clone();
            }
            catch
            {
                continue;
            }

            if (message.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString() ?? "";
                // Simulate a wedged agent that stops draining stdin but stays
                // alive: the OS pipe buffer then fills, so the client's writer
                // pump must absorb a full pipe without ever blocking the caller.
                if (method == "test/stall_stdin")
                {
                    await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
                    return;
                }
                if (message.TryGetProperty("id", out _))
                {
                    _ = HandleRequestAsync(message.Clone(), method);
                }
                else
                    await HandleNotificationAsync(message, method).ConfigureAwait(false);
                continue;
            }

            if (message.TryGetProperty("id", out var responseId)
                && _clientResponses.TryRemove(IdKey(responseId), out var pending))
            {
                pending.TrySetResult(message.Clone());
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement message, string method)
    {
        var id = message.GetProperty("id").Clone();
        var parameters = message.TryGetProperty("params", out var value) ? value.Clone() : default;

        switch (method)
        {
            case "test/echo":
                var delay = GetInt(parameters, "delayMs");
                if (delay > 0)
                    await Task.Delay(delay).ConfigureAwait(false);
                WriteResult(id, new { method, parameters });
                break;
            case "test/notify":
                WriteNotification("test/notification", new { value = "from-agent" });
                WriteResult(id, new { delivered = true });
                break;
            case "test/agent_request":
                var response = await SendClientRequestAsync("client/confirm", new { prompt = "Confirm" }).ConfigureAwait(false);
                WriteResult(id, new
                {
                    clientResult = response.TryGetProperty("result", out var result) ? result.Clone() : default
                });
                break;
            case "test/hang":
                break;
            case "test/pid":
                WriteResult(id, new { processId = Environment.ProcessId });
                break;
            case "test/stderr":
                Console.Error.WriteLine("fake-agent diagnostic");
                WriteResult(id, new { written = true });
                break;
            case "test/malformed":
                WriteRaw("{malformed-json");
                break;
            case "test/exit":
                Environment.Exit(23);
                break;
            case "initialize":
                _clientSupportsBooleanConfigOptions = HasBooleanConfigCapability(parameters);
                WriteResult(id, new
                {
                    protocolVersion = 1,
                    agentInfo = new { name = "PSX Fake ACP Agent", version = "1.0-test" },
                    agentCapabilities = _advertiseResume
                        ? (object)new
                        {
                            promptCapabilities = new { image = true },
                            // Spec shape: capability presence with an empty object.
                            sessionCapabilities = new { resume = new { } }
                        }
                        : new { promptCapabilities = new { image = true } },
                    authMethods = _authGate.Length > 0
                        ? new object[] { new { id = "login", name = "Log in", description = "Terminal auth" } }
                        : Array.Empty<object>()
                });
                break;
            case "authenticate":
                if (_authMode == "blocked")
                    WriteError(id, -32000, "Authentication required");
                else
                {
                    _authenticated = true;
                    WriteResult(id, new { });
                }
                break;
            case "session/new":
                if (ShouldFailAuth("session/new"))
                {
                    WriteError(id, -32000, "Authentication required");
                    break;
                }
                WriteResult(id, SessionResult("fake-session-new"));
                break;
            case "session/load":
                if (ShouldFailAuth("session/load"))
                {
                    WriteError(id, -32000, "Authentication required");
                    break;
                }
                if (_advertiseResume && !_resumeMethodMissing && _authGate != "resume")
                {
                    // Resume is implemented in this scenario: a session/load
                    // here means the client picked the wrong restore path.
                    WriteError(id, -32050, "session/load must not be used when resume is supported");
                    break;
                }
                await ReplayHistoryAsync(parameters).ConfigureAwait(false);
                WriteResult(id, SessionResult(GetString(parameters, "sessionId", "fake-session-loaded")));
                break;
            case "session/resume":
                if (ShouldFailAuth("session/resume"))
                {
                    WriteError(id, -32000, "Authentication required");
                    break;
                }
                if (_resumeMethodMissing)
                {
                    WriteError(id, -32601, "Method not found");
                    break;
                }
                // Per spec, resume restores context without replaying history;
                // control updates may still arrive as ordinary notifications.
                WriteSessionUpdate(new
                {
                    sessionUpdate = "available_commands_update",
                    availableCommands = new[] { new { name = "/resumed", description = "Resumed command" } }
                });
                WriteSessionUpdate(new { sessionUpdate = "usage_update", used = 888, size = 50_000 });
                WriteResult(id, new { });
                break;
            case "session/prompt":
                if (ShouldFailAuth("session/prompt"))
                {
                    WriteError(id, -32000, "Authentication required");
                    break;
                }
                _pendingPrompts[IdKey(id)] = id;
                await HandlePromptAsync(id, parameters).ConfigureAwait(false);
                break;
            case "session/set_mode":
                WriteResult(id, new { currentModeId = GetString(parameters, "modeId") });
                break;
            case "session/set_config_option":
                var isBoolean = GetString(parameters, "type") == "boolean";
                WriteResult(id, SessionResult(
                    "fake-session-new",
                    isBoolean && GetBoolean(parameters, "value")));
                break;
            default:
                WriteResult(id, new { });
                break;
        }
    }

    private async Task HandlePromptAsync(JsonElement id, JsonElement parameters)
    {
        var text = ReadPromptText(parameters);
        if (text.Contains("hang", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "agent_thought_chunk",
                content = new { type = "text", text = "Waiting for cancellation" }
            });
            return;
        }

        // Streams a partial assistant answer and then kills the process mid-turn
        // to simulate a real transport disconnect (process exit -> stdout EOF).
        // The client must persist the partial text and enter recovery_pending.
        if (text.Contains("crash after partial", StringComparison.OrdinalIgnoreCase))
        {
            WriteAssistantChunk("Partial answer before crash.");
            Environment.Exit(37);
            return;
        }

        // A plan update that carries only a document (no checklist entries)
        // must reach the frontend in document mode instead of being dropped.
        if (text.Contains("plan document", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "plan",
                content = new object[]
                {
                    new { type = "text", text = "## Approach\n\nRefactor first, then test." }
                }
            });
            WriteAssistantChunk("Plan document sent.");
            CompletePrompt(id, new { stopReason = "end_turn" });
            return;
        }

        // Tool merger: content wins over a differently formatted rawOutput.
        if (text.Contains("merge content rawoutput", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-merge-content",
                title = "Read",
                kind = "read",
                status = "in_progress",
                rawInput = new { file_path = "src/a.ts" }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-content",
                title = "Read",
                kind = "read",
                status = "completed",
                content = new[]
                {
                    new
                    {
                        type = "content",
                        content = new { type = "text", text = "```ts\n1| const x = 1;\n```" }
                    }
                },
                rawOutput = "const x = 1;"
            });
            WriteAssistantChunk("Merge content rawoutput done.");
            CompletePrompt(id, new { stopReason = "end_turn" });
            return;
        }

        // Tool merger: pending JSON param snapshots stay off the UI until rawInput.
        if (text.Contains("merge pending params", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-merge-pending",
                title = "Write",
                kind = "edit",
                status = "in_progress",
                content = new[]
                {
                    new
                    {
                        type = "content",
                        content = new { type = "text", text = "{\"path\":\"a.ts\",\"partial\":true}" }
                    }
                }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-pending",
                title = "Write",
                kind = "edit",
                status = "in_progress",
                content = new[]
                {
                    new
                    {
                        type = "content",
                        content = new { type = "text", text = "{\"path\":\"a.ts\",\"content\":\"final\"}" }
                    }
                }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-pending",
                title = "Write",
                kind = "edit",
                status = "in_progress",
                rawInput = new { path = "a.ts", content = "final" }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-pending",
                title = "Write",
                kind = "edit",
                status = "completed",
                content = new[]
                {
                    new
                    {
                        type = "content",
                        content = new { type = "text", text = "Wrote a.ts" }
                    }
                }
            });
            WriteAssistantChunk("Merge pending params done.");
            CompletePrompt(id, new { stopReason = "end_turn" });
            return;
        }

        // Tool merger: terminal deltas are overwritten by a later content snapshot.
        if (text.Contains("merge terminal snapshot", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-merge-terminal",
                title = "Bash",
                kind = "execute",
                status = "in_progress",
                rawInput = new { command = "echo hi" }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-terminal",
                title = "Bash",
                kind = "execute",
                status = "in_progress",
                _meta = new { terminal_output = new { data = "partial line\n" } }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-terminal",
                title = "Bash",
                kind = "execute",
                status = "completed",
                content = new[]
                {
                    new
                    {
                        type = "content",
                        content = new { type = "text", text = "final snapshot" }
                    }
                }
            });
            WriteAssistantChunk("Merge terminal snapshot done.");
            CompletePrompt(id, new { stopReason = "end_turn" });
            return;
        }

        // Tool merger: a later title-only update replaces name/summary via tool_updated.
        if (text.Contains("merge title update", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-merge-title",
                title = "Initial Title",
                kind = "read",
                status = "in_progress",
                rawInput = new { path = "x.ts" }
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-title",
                title = "Updated Title",
                kind = "read",
                status = "in_progress"
            });
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-merge-title",
                title = "Updated Title",
                kind = "read",
                status = "completed",
                content = new[]
                {
                    new
                    {
                        type = "content",
                        content = new { type = "text", text = "done" }
                    }
                }
            });
            WriteAssistantChunk("Merge title update done.");
            CompletePrompt(id, new { stopReason = "end_turn" });
            return;
        }

        // Diff-only document permission must keep a non-empty documentText.
        if (text.Contains("diff permission", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-diff-permission",
                title = "Apply patch",
                kind = "edit",
                status = "in_progress"
            });
            var diffPermission = await SendClientRequestAsync("session/request_permission", new
            {
                sessionId = GetString(parameters, "sessionId", "fake-session-new"),
                toolCall = new
                {
                    toolCallId = "tool-diff-permission",
                    title = "Apply patch",
                    kind = "edit",
                    status = "pending",
                    content = new object[]
                    {
                        new
                        {
                            type = "diff",
                            path = "src/App.cs",
                            oldText = "old line",
                            newText = "new line"
                        }
                    }
                },
                options = new[]
                {
                    new { optionId = "approve", name = "Approve", kind = "allow_once" },
                    new { optionId = "reject", name = "Reject", kind = "reject_once" }
                }
            }).ConfigureAwait(false);

            var diffOptionId = ReadPermissionOption(diffPermission);
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-diff-permission",
                title = "Apply patch",
                kind = "edit",
                status = "completed"
            });
            WriteAssistantChunk($"Diff permission result: {diffOptionId}");
            CompletePrompt(id, new { stopReason = "end_turn" });
            return;
        }

        WriteSessionUpdate(new
        {
            sessionUpdate = "available_commands_update",
            availableCommands = new[]
            {
                new { name = "/review", description = "Review changes" },
                new { name = "/review", description = "Duplicate should be removed" }
            }
        });
        WriteSessionUpdate(new
        {
            sessionUpdate = "agent_thought_chunk",
            content = new { type = "text", text = "Inspecting" }
        });
        WriteSessionUpdate(new
        {
            sessionUpdate = "plan",
            entries = new[]
            {
                new { content = "Inspect", status = "completed", priority = "high" },
                new { content = "Verify", status = "in_progress", priority = "medium" }
            }
        });
        WriteSessionUpdate(new
        {
            sessionUpdate = "tool_call",
            toolCallId = "tool-basic",
            title = "Read File",
            kind = "read",
            status = "in_progress",
            rawInput = new { path = "README.md" }
        });
        WriteSessionUpdate(new
        {
            sessionUpdate = "tool_call_update",
            toolCallId = "tool-basic",
            title = "Read File",
            kind = "read",
            status = "completed",
            content = new[]
            {
                new { type = "content", content = new { type = "text", text = "file contents" } }
            }
        });

        if (text.Contains("empty permission", StringComparison.OrdinalIgnoreCase))
        {
            var permission = await SendClientRequestAsync("session/request_permission", new
            {
                sessionId = GetString(parameters, "sessionId", "fake-session-new"),
                toolCall = new
                {
                    toolCallId = "tool-empty-permission",
                    title = "Empty permission",
                    kind = "execute",
                    status = "pending"
                },
                options = Array.Empty<object>()
            }).ConfigureAwait(false);
            WriteAssistantChunk($"Empty permission result: {ReadPermissionOption(permission)}");
        }
        else if (text.Contains("document permission", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-document-permission",
                title = "ExitPlanMode",
                status = "in_progress"
            });
            var permission = await SendClientRequestAsync("session/request_permission", new
            {
                sessionId = GetString(parameters, "sessionId", "fake-session-new"),
                toolCall = new
                {
                    toolCallId = "tool-document-permission",
                    title = "ExitPlanMode",
                    status = "pending",
                    content = new object[]
                    {
                        new { type = "text", text = "# Kimi plan\n\n1. Review the request\n2. Implement the change" },
                        new { type = "text", text = "Plan saved for approval." }
                    }
                },
                options = new[]
                {
                    new { optionId = "approve", name = "Approve", kind = "allow_once" },
                    new { optionId = "revise", name = "Revise", kind = "reject_once" },
                    new { optionId = "reject", name = "Reject and Exit", kind = "reject_once" }
                }
            }).ConfigureAwait(false);

            var optionId = ReadPermissionOption(permission);
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-document-permission",
                title = "ExitPlanMode",
                status = "completed"
            });
            WriteAssistantChunk($"Document permission result: {optionId}");
        }
        else if (text.Contains("ordinary permission", StringComparison.OrdinalIgnoreCase))
        {
            var permission = await SendClientRequestAsync("session/request_permission", new
            {
                sessionId = GetString(parameters, "sessionId", "fake-session-new"),
                toolCall = new
                {
                    toolCallId = "tool-ordinary-permission",
                    title = "Run command?",
                    kind = "execute",
                    status = "pending",
                    content = new[] { new { type = "text", text = "# Markdown command details" } }
                },
                options = new[]
                {
                    new { optionId = "run-once", name = "Run once", kind = "allow_once" },
                    new { optionId = "cancel", name = "Cancel", kind = "reject_once" }
                }
            }).ConfigureAwait(false);
            WriteAssistantChunk($"Ordinary permission result: {ReadPermissionOption(permission)}");
        }
        else if (text.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "tool-mode-transition",
                title = "Ready to code?",
                kind = "switch_mode",
                status = "in_progress"
            });
            var permission = await SendClientRequestAsync("session/request_permission", new
            {
                sessionId = GetString(parameters, "sessionId", "fake-session-new"),
                toolCall = new
                {
                    toolCallId = "tool-mode-transition",
                    title = "Ready to code?",
                    kind = "switch_mode",
                    status = "pending",
                    content = new object[]
                    {
                        new { type = "content", content = new { type = "text", text = "# Fake plan" } },
                        new { type = "text", text = "Implement and verify." }
                    }
                },
                options = new[]
                {
                    new { optionId = "approve", name = "Approve once", kind = "allow_once" },
                    new { optionId = "reject", name = "Keep planning", kind = "reject_once" }
                }
            }).ConfigureAwait(false);

            var optionId = ReadPermissionOption(permission);
            WriteSessionUpdate(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "tool-mode-transition",
                title = "Ready to code?",
                kind = "switch_mode",
                status = "completed"
            });
            WriteAssistantChunk($"Permission result: {optionId}");
        }
        else
        {
            WriteAssistantChunk("Fake response completed.");
        }

        WriteSessionUpdate(new
        {
            sessionUpdate = "usage_update",
            used = 4321,
            size = 100_000,
            cost = new { amount = 0.23m, currency = "USD" }
        });
        CompletePrompt(id, new { stopReason = "end_turn" });
    }

    private async Task ReplayHistoryAsync(JsonElement parameters)
    {
        var sessionId = GetString(parameters, "sessionId", "fake-session-loaded");
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "user_message_chunk",
            content = new { type = "text", text = "Historical user" }
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "agent_thought_chunk",
            content = new { type = "text", text = "Historical thought one." }
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "agent_message_chunk",
            content = new { type = "text", text = "Historical assistant before tool." }
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "tool_call",
            toolCallId = "stored-tool",
            title = "Ready?",
            kind = "switch_mode",
            status = "completed"
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "agent_thought_chunk",
            content = new { type = "text", text = "Historical thought two." }
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "agent_message_chunk",
            content = new { type = "text", text = "Historical assistant after tool." }
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "plan",
            entries = new[] { new { content = "Historical step", status = "completed", priority = "medium" } }
        });
        // Control updates interleaved with the replay: a client that keeps its
        // local transcript must still apply these instead of dropping them
        // together with the content chunks.
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "available_commands_update",
            availableCommands = new[] { new { name = "/replayed", description = "Replayed command" } }
        });
        WriteSessionUpdate(new { sessionId, sessionUpdate = "usage_update", used = 777, size = 50_000 });
        await Task.Yield();
    }

    private Task HandleNotificationAsync(JsonElement message, string method)
    {
        if (method != "session/cancel")
            return Task.CompletedTask;

        if (_ignoreCancel)
            return Task.CompletedTask;

        foreach (var pending in _pendingPrompts.ToArray())
            CompletePrompt(pending.Value, new { stopReason = "cancelled" });
        return Task.CompletedTask;
    }

    private async Task<JsonElement> SendClientRequestAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref _nextClientRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientResponses[id.ToString()] = completion;
        Write(new { jsonrpc = "2.0", id, method, @params = parameters });
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private void WriteAssistantChunk(string text)
    {
        WriteSessionUpdate(new
        {
            sessionUpdate = "agent_message_chunk",
            content = new { type = "text", text }
        });
    }

    private void WriteSessionUpdate(object update)
    {
        WriteNotification("session/update", new { sessionId = "fake-session-new", update });
    }

    private void WriteNotification(string method, object parameters)
    {
        Write(new { jsonrpc = "2.0", method, @params = parameters });
    }

    private void CompletePrompt(JsonElement id, object result)
    {
        _pendingPrompts.TryRemove(IdKey(id), out _);
        WriteResult(id, result);
    }

    private void WriteResult(JsonElement id, object result)
    {
        Write(new { jsonrpc = "2.0", id, result });
    }

    private void WriteError(JsonElement id, int code, string message)
    {
        Write(new { jsonrpc = "2.0", id, error = new { code, message } });
    }

    private bool ShouldFailAuth(string method)
    {
        var gatedMethod = _authGate switch
        {
            "new" => "session/new",
            "load" => "session/load",
            "resume" => "session/resume",
            "prompt" => "session/prompt",
            _ => ""
        };
        if (method != gatedMethod)
            return false;
        return _authMode switch
        {
            "blocked" => true,
            "recover" => !_authenticated,
            _ => false
        };
    }

    private void Write(object message)
    {
        WriteRaw(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void WriteRaw(string line)
    {
        lock (_outputLock)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private object SessionResult(string sessionId, bool fastMode = false) => new
    {
        sessionId,
        modes = new
        {
            currentModeId = "plan",
            availableModes = new[]
            {
                new { id = "plan", name = "Plan", description = "Plan changes" },
                new { id = "code", name = "Code", description = "Apply changes" }
            }
        },
        configOptions = _clientSupportsBooleanConfigOptions
            ? new object[]
            {
                new
                {
                    id = "mode",
                    name = "Mode",
                    description = "Agent mode",
                    category = "general",
                    type = "select",
                    currentValue = "plan",
                    options = new[]
                    {
                        new { value = "plan", name = "Plan", description = "Plan changes" },
                        new { value = "code", name = "Code", description = "Apply changes" }
                    }
                },
                new
                {
                    id = "fast_mode",
                    name = "Fast mode",
                    description = "Use fast mode",
                    category = "general",
                    type = "boolean",
                    currentValue = fastMode
                }
            }
            : new object[]
            {
                new
                {
                    id = "mode",
                    name = "Mode",
                    description = "Agent mode",
                    category = "general",
                    type = "select",
                    currentValue = "plan",
                    options = new[]
                    {
                        new { value = "plan", name = "Plan", description = "Plan changes" },
                        new { value = "code", name = "Code", description = "Apply changes" }
                    }
                }
            }
    };

    private static string ReadPromptText(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("prompt", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join("\n", blocks.EnumerateArray()
            .Where(block => GetString(block, "type") == "text")
            .Select(block => GetString(block, "text")));
    }

    private static string ReadPermissionOption(JsonElement permission)
    {
        return permission.TryGetProperty("result", out var permissionResult)
               && permissionResult.TryGetProperty("outcome", out var outcome)
               && outcome.TryGetProperty("optionId", out var selected)
            ? selected.GetString() ?? "cancelled"
            : "cancelled";
    }

    private static int GetInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static string GetString(JsonElement element, string name, string fallback = "")
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    private static bool HasBooleanConfigCapability(JsonElement parameters)
    {
        return parameters.ValueKind == JsonValueKind.Object
               && parameters.TryGetProperty("clientCapabilities", out var capabilities)
               && capabilities.TryGetProperty("session", out var session)
               && session.TryGetProperty("configOptions", out var configOptions)
               && configOptions.TryGetProperty("boolean", out var boolean)
               && boolean.ValueKind == JsonValueKind.Object;
    }

    private static string IdKey(JsonElement id)
    {
        return id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText();
    }
}
