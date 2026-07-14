using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

public sealed class ClaudeCodeAgentService : IAgentSessionService
{
    private readonly IAgentBridgeService _bridgeService;
    private readonly ITabManagementService _tabManagementService;
    private readonly ITerminalBridgeService _terminalBridgeService;
    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentDirectoryPicker _directoryPicker;
    private readonly object _runLock = new();
    private AgentThread _currentThread;
    private Process? _currentProcess;
    private string _workingDirectory = "";
    private string? _claudeSessionId;
    private string _status = "ready";
    private bool _isRunning;
    private bool _disposed;
    private bool _receivedPartialText;
    private bool _toolInProgress;
    private readonly StringBuilder _assistantBuffer = new();
    private readonly StringBuilder _toolBuffer = new();
    private string _toolName = "Tool";
    private string? _currentRunId;
    private string? _currentToolCallId;
    private string _currentToolSummary = "";
    private int _toolCallCounter;

    public ClaudeCodeAgentService(
        IAgentBridgeService bridgeService,
        ITabManagementService tabManagementService,
        ITerminalBridgeService terminalBridgeService,
        IAgentThreadStore threadStore,
        IAgentDirectoryPicker directoryPicker)
    {
        _bridgeService = bridgeService;
        _tabManagementService = tabManagementService;
        _terminalBridgeService = terminalBridgeService;
        _threadStore = threadStore;
        _directoryPicker = directoryPicker;
        _currentThread = _threadStore.LoadOrCreateInitialThread(ResolveWorkspaceRoot());
        ApplyThread(_currentThread);
        _bridgeService.UserMessageSubmitted += OnUserMessageSubmitted;
        _bridgeService.CommandReceived += OnCommandReceived;
    }

    public Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null)
    {
        if (_disposed)
            return Task.CompletedTask;

        if (attachmentIds?.Count > 0)
        {
            return _bridgeService.SendEventAsync(new
            {
                type = "run_failed",
                text = "Image attachments are only supported by the ACP Agent backend."
            });
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('/') && TryHandlePsxSlashCommand(trimmed, out var commandTask))
            return commandTask;

        return StartClaudeRunAsync(trimmed);
    }

    public async Task ClearAsync()
    {
        _currentThread.Messages.Clear();
        SaveCurrentThread();
        await _bridgeService.SendEventAsync(new { type = "agent_cleared" });
    }

    public async Task NewThreadAsync(string? workingDirectory = null)
    {
        await CancelAsync(silent: true);
        var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? _workingDirectory : workingDirectory;
        _currentThread = _threadStore.CreateThread(cwd);
        ApplyThread(_currentThread);
        await SendThreadLoadedAsync(clear: true, selectPlan: true);
        await _bridgeService.SendEventAsync(new { type = "command_result", text = "Started a new Agent thread." });
        await PublishStateAsync();
    }

    public async Task LoadThreadAsync(string threadId)
    {
        var thread = _threadStore.LoadThread(threadId);
        if (thread == null)
        {
            await _bridgeService.SendEventAsync(new { type = "run_failed", text = $"Thread not found: {threadId}" });
            return;
        }

        await CancelAsync(silent: true);
        _currentThread = thread;
        ApplyThread(_currentThread);
        _threadStore.SaveLastThread(_currentThread);
        await SendThreadLoadedAsync(clear: true);
        await PublishStateAsync();
    }

    public async Task DeleteThreadAsync(string threadId)
    {
        await CancelAsync(silent: true);
        _threadStore.DeleteThread(threadId);
        AgentThreadSummary? next = null;
        try
        {
            next = _threadStore.ListThreads().FirstOrDefault();
        }
        catch (Exception ex)
        {
            await SendHistoryErrorAsync(ex);
        }
        _currentThread = next != null
            ? _threadStore.LoadThread(next.ThreadId) ?? _threadStore.CreateThread(_workingDirectory)
            : _threadStore.CreateThread(_workingDirectory);
        ApplyThread(_currentThread);
        await SendThreadLoadedAsync(clear: true);
        await _bridgeService.SendEventAsync(new { type = "command_result", text = "Deleted thread." });
        await PublishStateAsync();
    }

    public async Task ChangeDirectoryAsync(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        var fullPath = Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(_workingDirectory, expanded));

        if (!Directory.Exists(fullPath))
        {
            await _bridgeService.SendEventAsync(new { type = "run_failed", text = $"Directory does not exist: {fullPath}" });
            return;
        }

        await NewThreadAsync(fullPath);
        await _bridgeService.SendEventAsync(new { type = "command_result", text = $"Working directory changed to: {fullPath}" });
    }

    public async Task ListThreadsAsync()
    {
        try
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "agent_threads",
                threads = _threadStore.ListThreads().Select(t => new
                {
                    threadId = t.ThreadId,
                    title = t.Title,
                    cwd = t.Cwd,
                    sessionId = t.ClaudeSessionId ?? "",
                    updatedAt = t.UpdatedAt.ToString("u")
                }).ToArray()
            });
        }
        catch (Exception ex)
        {
            await SendHistoryErrorAsync(ex);
        }
    }

    private Task SendHistoryErrorAsync(Exception exception)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_history_error",
            text = $"Unable to load Agent thread history. {exception.Message}"
        });
    }

    public Task PublishStateAsync()
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_state",
            cwd = _workingDirectory,
            sessionId = _claudeSessionId ?? "",
            threadId = _currentThread.ThreadId,
            title = _currentThread.Title,
            status = _status,
            busy = _isRunning,
            store = _threadStore.RootDirectory
        });
    }

    public Task CancelAsync()
    {
        return CancelAsync(silent: false);
    }

    private async Task CancelAsync(bool silent)
    {
        Process? process;
        lock (_runLock)
        {
            process = _currentProcess;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        lock (_runLock)
        {
            _currentProcess = null;
            _isRunning = false;
            _status = "ready";
            _currentRunId = null;
            _currentToolCallId = null;
            _toolCallCounter = 0;
            _currentToolSummary = "";
        }

        if (!silent)
            await _bridgeService.SendEventAsync(new { type = "command_result", text = "Claude run stopped." });
        await PublishStateAsync();
    }

    private void OnUserMessageSubmitted(object? sender, AgentSubmitEventArgs e)
    {
        _ = SubmitMessageAsync(e.Text);
    }

    private void OnCommandReceived(object? sender, AgentCommandEventArgs e)
    {
        _ = HandleFrontendCommandAsync(e);
    }

    private async Task HandleFrontendCommandAsync(AgentCommandEventArgs e)
    {
        switch (e.Command)
        {
            case "state":
                await SendThreadLoadedAsync(clear: true);
                await PublishStateAsync();
                break;
            case "clear":
                await ClearAsync();
                break;
            case "new":
                await NewThreadAsync();
                break;
            case "cwd":
                if (string.IsNullOrWhiteSpace(e.Value))
                    await _bridgeService.SendEventAsync(new { type = "command_result", text = $"Current working directory: {_workingDirectory}" });
                else
                    await ChangeDirectoryAsync(e.Value);
                break;
            case "pick_cwd":
                await PickDirectoryAsync();
                break;
            case "terminal":
                await OpenRawClaudeTerminalAsync();
                break;
            case "stop":
                await CancelAsync();
                break;
            case "history":
                await ListThreadsAsync();
                break;
            case "load_thread":
                if (!string.IsNullOrWhiteSpace(e.Value))
                    await LoadThreadAsync(e.Value);
                break;
            case "delete":
                await DeleteThreadAsync(_currentThread.ThreadId);
                break;
            case "help":
                await SendHelpAsync();
                break;
            case "claude_command":
                if (!string.IsNullOrWhiteSpace(e.Value))
                    await StartClaudeRunAsync(e.Value);
                break;
            case "agent_permission_response":
            case "agent_question_response":
                await OpenRawClaudeTerminalAsync();
                break;
        }
    }

    private bool TryHandlePsxSlashCommand(string commandText, out Task task)
    {
        var parts = commandText.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var value = parts.Length > 1 ? parts[1] : "";

        task = command switch
        {
            "/clear" => ClearAsync(),
            "/new" => NewThreadAsync(),
            "/cwd" => string.IsNullOrWhiteSpace(value)
                ? _bridgeService.SendEventAsync(new { type = "command_result", text = $"Current working directory: {_workingDirectory}" })
                : ChangeDirectoryAsync(value),
            "/terminal" => OpenRawClaudeTerminalAsync(),
            "/stop" => CancelAsync(),
            "/history" => ListThreadsAsync(),
            "/delete" => DeleteThreadAsync(_currentThread.ThreadId),
            "/help" => SendHelpAsync(),
            _ => Task.CompletedTask
        };

        return command is "/clear" or "/new" or "/cwd" or "/terminal" or "/stop" or "/history" or "/delete" or "/help";
    }

    private async Task PickDirectoryAsync()
    {
        var selected = _directoryPicker.PickDirectory(_workingDirectory);
        if (!string.IsNullOrWhiteSpace(selected))
            await ChangeDirectoryAsync(selected);
    }

    private Task SendHelpAsync()
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "command_result",
            text = "PSX commands: /clear, /new, /cwd, /cwd <path>, /terminal, /stop, /history, /delete, /help. Claude Code commands are sent through unchanged."
        });
    }

    private async Task OpenRawClaudeTerminalAsync()
    {
        var escapedCwd = _workingDirectory.Replace("'", "''");
        var claudeCommand = string.IsNullOrWhiteSpace(_claudeSessionId)
            ? "claude"
            : $"claude --resume {_claudeSessionId}";
        var profile = new ShellProfile
        {
            Id = "claude-code",
            Name = "Claude Code",
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {claudeCommand}",
            StartingDirectory = _workingDirectory
        };

        await _tabManagementService.CreateTabAsync(profile);
        await _terminalBridgeService.SetViewModeAsync("terminal");
        await _bridgeService.SendEventAsync(new
        {
            type = "raw_terminal_fallback",
            text = "Opened a raw Claude Code terminal tab in the current working directory."
        });
        _status = "fallback";
        await PublishStateAsync();
    }

    private async Task StartClaudeRunAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        if (!Directory.Exists(_workingDirectory))
        {
            await _bridgeService.SendEventAsync(new { type = "run_failed", text = $"Directory does not exist: {_workingDirectory}", runId = (string?)null });
            return;
        }

        lock (_runLock)
        {
            if (_isRunning)
            {
                _ = _bridgeService.SendEventAsync(new
                {
                    type = "run_failed",
                    text = "Claude is still responding. Wait for the current run to finish or use /stop.",
                    runId = (string?)null
                });
                return;
            }

            _isRunning = true;
            _status = "running";
            _receivedPartialText = false;
            _toolInProgress = false;
            _assistantBuffer.Clear();
            _toolBuffer.Clear();
            _currentRunId = Guid.NewGuid().ToString();
            _toolCallCounter = 0;
        }

        AddMessage("user", prompt);
        await _bridgeService.SendEventAsync(new { type = "user_message", text = prompt, runId = _currentRunId });
        await _bridgeService.SendEventAsync(new { type = "thinking_started" });
        await PublishStateAsync();
        _ = Task.Run(() => RunClaudeAsync(prompt));
    }

    private async Task RunClaudeAsync(string prompt)
    {
        var command = ResolveClaudeCommand();
        if (command == null)
        {
            await FinishRunWithError("Claude Code was not found on PATH. Install Claude Code or make sure the `claude` command is available.");
            return;
        }

        var startInfo = BuildStartInfo(command, prompt);
        var stderr = new StringBuilder();
        var attemptedResume = !string.IsNullOrWhiteSpace(_claudeSessionId);

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            lock (_runLock)
            {
                _currentProcess = process;
            }

            if (!process.Start())
            {
                await FinishRunWithError("Failed to start Claude Code.");
                return;
            }

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        stderr.AppendLine(line);
                }
            });

            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    await HandleClaudeLineAsync(line).ConfigureAwait(false);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = stderr.Length > 0
                    ? stderr.ToString().Trim()
                    : $"Claude Code exited with code {process.ExitCode}.";
                await _bridgeService.SendEventAsync(new { type = "run_failed", text = error, runId = _currentRunId });
                if (attemptedResume)
                {
                    await _bridgeService.SendEventAsync(new
                    {
                        type = "resume_failed",
                        message = "PSX could not restore the Agent session. The saved local transcript is still available to read.",
                        detail = error
                    });
                }
                _status = "error";
            }

            await FinishAssistantMessageAsync();
            await _bridgeService.SendEventAsync(new { type = "thinking_finished" });
            await _bridgeService.SendEventAsync(new { type = "assistant_message_done" });
        }
        catch (Exception ex)
        {
            await FinishRunWithError(ex.Message);
        }
        finally
        {
            var completedRunId = _currentRunId;
            await SendToolFinishedIfNeededAsync("interrupted");
            await _bridgeService.SendEventAsync(new { type = "run_finished", runId = completedRunId });
            lock (_runLock)
            {
                _currentProcess = null;
                _isRunning = false;
                if (_status == "running")
                    _status = "ready";
            }
            SaveCurrentThread();
            await PublishStateAsync();
        }
    }

    private ProcessStartInfo BuildStartInfo(ClaudeCommand command, string prompt)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.PrefixArguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--include-partial-messages");
        startInfo.ArgumentList.Add("--verbose");

        if (!string.IsNullOrWhiteSpace(_claudeSessionId))
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(_claudeSessionId);
        }

        startInfo.ArgumentList.Add(prompt);
        return startInfo;
    }

    private async Task HandleClaudeLineAsync(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = GetString(root, "type");
            await CaptureSessionIdAsync(root);

            if (type == "stream_event" && root.TryGetProperty("event", out var streamEvent))
            {
                await HandleClaudeStreamEventAsync(streamEvent);
                return;
            }

            switch (type)
            {
                case "system":
                    await CaptureClaudeCommandsAsync(root);
                    await _bridgeService.SendEventAsync(new
                    {
                        type = "agent_ready",
                        sessionId = GetString(root, "session_id")
                    });
                    break;
                case "assistant":
                    await HandleAssistantMessageAsync(root);
                    break;
                case "result":
                    await _bridgeService.SendEventAsync(new { type = "thinking_finished" });
                    break;
                case "content_block_start":
                    await HandleContentBlockStartAsync(root);
                    break;
                case "content_block_delta":
                    await HandleContentBlockDeltaAsync(root);
                    break;
                case "content_block_stop":
                    await SendToolFinishedIfNeededAsync();
                    break;
                case "message_stop":
                    await _bridgeService.SendEventAsync(new { type = "assistant_message_done" });
                    break;
                default:
                    await TrySendKnownContentAsync(root, line);
                    break;
            }
        }
        catch
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "tool_delta",
                name = "Claude stdout",
                text = line
            });
        }
    }

    private async Task HandleClaudeStreamEventAsync(JsonElement streamEvent)
    {
        var type = GetString(streamEvent, "type");
        switch (type)
        {
            case "content_block_start":
                await HandleContentBlockStartAsync(streamEvent);
                break;
            case "content_block_delta":
                await HandleContentBlockDeltaAsync(streamEvent);
                break;
            case "content_block_stop":
                await SendToolFinishedIfNeededAsync();
                break;
            case "message_stop":
                await _bridgeService.SendEventAsync(new { type = "assistant_message_done" });
                break;
            case "message_delta":
                if (streamEvent.TryGetProperty("delta", out var delta)
                    && GetString(delta, "stop_reason") is { Length: > 0 })
                {
                    await _bridgeService.SendEventAsync(new { type = "thinking_finished" });
                }
                break;
        }
    }

    private async Task CaptureSessionIdAsync(JsonElement root)
    {
        var sessionId = GetString(root, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _claudeSessionId = sessionId;
        _currentThread.ClaudeSessionId = sessionId;
        SaveCurrentThread();
        await PublishStateAsync();
    }

    private Task CaptureClaudeCommandsAsync(JsonElement root)
    {
        if (!root.TryGetProperty("slash_commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
            return Task.CompletedTask;

        var names = commands.EnumerateArray()
            .Select(ReadSlashCommandName)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToArray();

        return _bridgeService.SendEventAsync(new
        {
            type = "agent_commands",
            commands = names
        });
    }

    private static string? ReadSlashCommandName(JsonElement command)
    {
        if (command.ValueKind == JsonValueKind.String)
            return NormalizeSlashCommand(command.GetString());

        if (command.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in new[] { "name", "command", "value" })
        {
            if (command.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return NormalizeSlashCommand(value.GetString());
        }

        return null;
    }

    private static string? NormalizeSlashCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private async Task HandleAssistantMessageAsync(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return;

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in content.EnumerateArray())
        {
            var itemType = GetString(item, "type");
            switch (itemType)
            {
                case "text":
                    if (!_receivedPartialText)
                    {
                        await SendAssistantTextAsync(GetString(item, "text"));
                    }
                    break;
                case "tool_use":
                    await StartToolAsync(GetString(item, "id"), GetString(item, "name"),
                        item.TryGetProperty("input", out var input) ? input.GetRawText() : "");
                    break;
            }
        }
    }

    private async Task HandleContentBlockStartAsync(JsonElement root)
    {
        if (!root.TryGetProperty("content_block", out var contentBlock))
            return;

        if (GetString(contentBlock, "type") == "tool_use")
        {
            await StartToolAsync(GetString(contentBlock, "id"), GetString(contentBlock, "name"),
                contentBlock.TryGetProperty("input", out var input) ? input.GetRawText() : "");
        }
    }

    private Task StartToolAsync(string id, string name, string input)
    {
        _toolInProgress = true;
        _toolName = string.IsNullOrWhiteSpace(name) ? "Tool" : name;
        _toolBuffer.Clear();
        if (!string.IsNullOrWhiteSpace(input))
            _toolBuffer.Append(input);

        _currentToolCallId = !string.IsNullOrWhiteSpace(id) ? id : Guid.NewGuid().ToString();
        _toolCallCounter++;
        _currentToolSummary = BuildToolSummary(_toolName, input);

        return _bridgeService.SendEventAsync(new
        {
            type = "tool_started",
            id,
            name = _toolName,
            input,
            runId = _currentRunId,
            toolCallId = _currentToolCallId,
            summary = _currentToolSummary
        });
    }

    private async Task SendToolFinishedIfNeededAsync(string status = "completed")
    {
        if (!_toolInProgress)
            return;

        _toolInProgress = false;
        var output = _toolBuffer.ToString();

        AddToolMessage(output, _toolName, _currentRunId, _currentToolCallId,
                       output, status, _currentToolSummary);

        await _bridgeService.SendEventAsync(new
        {
            type = "tool_finished",
            runId = _currentRunId,
            toolCallId = _currentToolCallId,
            summary = _currentToolSummary,
            status
        });

        _currentToolCallId = null;
        _currentToolSummary = "";
    }

    private async Task HandleContentBlockDeltaAsync(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var delta))
            return;

        var deltaType = GetString(delta, "type");
        switch (deltaType)
        {
            case "text_delta":
                _receivedPartialText = true;
                await SendAssistantTextAsync(GetString(delta, "text"));
                break;
            case "thinking_delta":
                await _bridgeService.SendEventAsync(new { type = "thinking_started" });
                break;
            case "input_json_delta":
                var partialJson = GetString(delta, "partial_json");
                _toolBuffer.Append(partialJson);
                await _bridgeService.SendEventAsync(new
                {
                    type = "tool_delta",
                    text = partialJson,
                    runId = _currentRunId,
                    toolCallId = _currentToolCallId
                });
                break;
        }
    }

    private static string BuildToolSummary(string name, string input)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input))
                return name;

            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            string? detail = null;

            if (name is "Read" or "Write" or "Edit" or "DeleteFile" or "InsertContent"
                or "ReplaceContent" or "DeleteLines" or "ReadLines")
            {
                if (root.TryGetProperty("file_path", out var fp))
                    detail = fp.GetString();
            }
            else if (name is "Bash" or "Shell")
            {
                if (root.TryGetProperty("command", out var cmd))
                    detail = cmd.GetString();
            }
            else if (name is "Grep" or "Search" or "RipGrep")
            {
                if (root.TryGetProperty("pattern", out var pat))
                    detail = "'" + pat.GetString() + "'";
            }
            else if (name is "Glob" or "GlobFiles")
            {
                if (root.TryGetProperty("pattern", out var pat))
                    detail = pat.GetString();
            }
            else
            {
                if (root.TryGetProperty("description", out var desc))
                    detail = desc.GetString();
                else if (root.TryGetProperty("file_path", out var fp))
                    detail = fp.GetString();
            }

            if (string.IsNullOrWhiteSpace(detail))
                return name;

            var summary = name + " " + detail;
            return summary.Length <= 80 ? summary : summary[..77] + "...";
        }
        catch
        {
            return name;
        }
    }

    private void AddToolMessage(string text, string name, string? runId,
        string? toolCallId, string? toolOutput, string? toolStatus, string? summary)
    {
        _currentThread.Messages.Add(new AgentMessage
        {
            Role = "tool",
            Text = text,
            Name = name,
            RunId = runId,
            ToolCallId = toolCallId,
            ToolInput = text,
            ToolOutput = toolOutput,
            ToolStatus = toolStatus,
            Summary = summary,
            CreatedAt = DateTimeOffset.Now
        });
        SaveCurrentThread();
    }

    private async Task TrySendKnownContentAsync(JsonElement root, string rawLine)
    {
        var type = GetString(root, "type");
        if (type.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "permission_request",
                requestId = Guid.NewGuid().ToString(),
                title = "Claude Code permission request",
                text = rawLine
            });
            return;
        }

        if (type.Contains("question", StringComparison.OrdinalIgnoreCase))
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "question_request",
                requestId = Guid.NewGuid().ToString(),
                title = "Claude Code question",
                text = rawLine
            });
            return;
        }

        if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            await SendAssistantTextAsync(text.GetString() ?? "");
        }
    }

    private Task SendAssistantTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        _assistantBuffer.Append(text);
        return _bridgeService.SendEventAsync(new { type = "assistant_delta", text });
    }

    private Task FinishAssistantMessageAsync()
    {
        var text = _assistantBuffer.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            AddMessage("assistant", text);

        _assistantBuffer.Clear();
        return Task.CompletedTask;
    }

    private async Task FinishRunWithError(string message)
    {
        await _bridgeService.SendEventAsync(new { type = "thinking_finished" });
        await _bridgeService.SendEventAsync(new { type = "run_failed", text = message, runId = _currentRunId });

        lock (_runLock)
        {
            _currentProcess = null;
            _isRunning = false;
            _status = "error";
        }
        SaveCurrentThread();
        await PublishStateAsync();
    }

    private void AddMessage(string role, string text, string? name = null)
    {
        if (role == "user" && _currentThread.Messages.Count == 0)
            _currentThread.Title = BuildMessageTitle(text);

        _currentThread.Messages.Add(new AgentMessage
        {
            Role = role,
            Text = text,
            Name = name,
            RunId = role is "user" or "assistant" ? _currentRunId : null,
            CreatedAt = DateTimeOffset.Now
        });
        SaveCurrentThread();
    }

    private void SaveCurrentThread()
    {
        _currentThread.Cwd = _workingDirectory;
        _currentThread.ClaudeSessionId = _claudeSessionId;
        _threadStore.SaveThread(_currentThread);
    }

    private static string BuildMessageTitle(string text)
    {
        var compact = string.Join(' ', text.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(compact))
            return "Agent Chat";

        return compact.Length <= 48 ? compact : compact[..48] + "...";
    }

    private void ApplyThread(AgentThread thread)
    {
        _workingDirectory = Directory.Exists(thread.Cwd) ? thread.Cwd : ResolveWorkspaceRoot();
        _claudeSessionId = thread.ClaudeSessionId;
        _status = "ready";
        _assistantBuffer.Clear();
        _toolBuffer.Clear();
        _toolInProgress = false;
        _currentRunId = null;
        _currentToolCallId = null;
        _toolCallCounter = 0;
        _currentToolSummary = "";
    }

    private Task SendThreadLoadedAsync(bool clear, bool selectPlan = false)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_thread_loaded",
            clear,
            selectPlan,
            threadId = _currentThread.ThreadId,
            title = _currentThread.Title,
            cwd = _currentThread.Cwd,
            sessionId = _currentThread.ClaudeSessionId ?? "",
            messages = _currentThread.Messages.Select(m => new
            {
                role = m.Role,
                text = m.Text,
                name = m.Name ?? "",
                createdAt = m.CreatedAt.ToString("u"),
                runId = m.RunId,
                toolCallId = m.ToolCallId,
                summary = m.Summary,
                toolInput = m.ToolInput,
                toolOutput = m.ToolOutput,
                toolStatus = m.ToolStatus
            }).ToArray()
        });
    }

    private static string ResolveWorkspaceRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PSX.csproj")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static ClaudeCommand? ResolveClaudeCommand()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude.bat", "claude.ps1" })
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (!File.Exists(candidate))
                    continue;

                if (candidate.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    return new ClaudeCommand("powershell.exe", new[]
                    {
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        candidate
                    });
                }

                return new ClaudeCommand(candidate, Array.Empty<string>());
            }
        }

        return null;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _bridgeService.UserMessageSubmitted -= OnUserMessageSubmitted;
        _bridgeService.CommandReceived -= OnCommandReceived;
        _ = CancelAsync(silent: true);
    }

    private sealed record ClaudeCommand(string FileName, IReadOnlyList<string> PrefixArguments);
}
