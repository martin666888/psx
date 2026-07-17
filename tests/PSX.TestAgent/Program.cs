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
    private int _nextClientRequestId = 7000;

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
                WriteResult(id, new
                {
                    protocolVersion = 1,
                    agentInfo = new { name = "PSX Fake ACP Agent", version = "1.0-test" },
                    agentCapabilities = new { promptCapabilities = new { image = true } }
                });
                break;
            case "session/new":
                WriteResult(id, SessionResult("fake-session-new"));
                break;
            case "session/load":
                await ReplayHistoryAsync(parameters).ConfigureAwait(false);
                WriteResult(id, SessionResult(GetString(parameters, "sessionId", "fake-session-loaded")));
                break;
            case "session/prompt":
                _pendingPrompts[IdKey(id)] = id;
                await HandlePromptAsync(id, parameters).ConfigureAwait(false);
                break;
            case "session/set_mode":
                WriteResult(id, new { currentModeId = GetString(parameters, "modeId") });
                break;
            case "session/set_config_option":
                WriteResult(id, SessionResult("fake-session-new"));
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

        WriteSessionUpdate(new { sessionUpdate = "usage_update", used = 4321 });
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
            sessionUpdate = "agent_message_chunk",
            content = new { type = "text", text = "Historical assistant" }
        });
        WriteSessionUpdate(new
        {
            sessionId,
            sessionUpdate = "plan",
            entries = new[] { new { content = "Historical step", status = "completed", priority = "medium" } }
        });
        await Task.Yield();
    }

    private Task HandleNotificationAsync(JsonElement message, string method)
    {
        if (method != "session/cancel")
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

    private static object SessionResult(string sessionId) => new
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
        configOptions = new[]
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

    private static string IdKey(JsonElement id)
    {
        return id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText();
    }
}
