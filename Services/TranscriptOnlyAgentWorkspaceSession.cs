using PSX.Models;

namespace PSX.Services;

internal sealed class TranscriptOnlyAgentWorkspaceSession : IAgentWorkspaceSession
{
    private readonly IAgentBridgeService _bridge;
    private readonly IAgentThreadStore _threadStore;
    private readonly AgentThread _thread;
    private bool _disposed;

    public TranscriptOnlyAgentWorkspaceSession(
        Guid workspaceId,
        IAgentBridgeService bridge,
        IAgentThreadStore threadStore,
        AgentThread thread)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Agent Workspace ID must not be empty.", nameof(workspaceId));

        WorkspaceId = workspaceId;
        _bridge = bridge;
        _threadStore = threadStore;
        _thread = thread;

        _bridge.UserMessageSubmitted += OnUserMessageSubmitted;
        _bridge.CommandReceived += OnCommandReceived;
        _bridge.AttachmentUploadReceived += OnAttachmentUploadReceived;
    }

    public Guid WorkspaceId { get; }
    public string ThreadId => _thread.ThreadId;
    public string ProviderKey => _thread.Provider;
    public string WorkingDirectory => _thread.Cwd;
    public bool IsDraft => false;

    public Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null) =>
        SendReadOnlyErrorAsync();

    public Task CancelAsync() => Task.CompletedTask;

    public Task ChangeDirectoryAsync(string path) => SendReadOnlyErrorAsync();

    public async Task ListThreadsAsync(string? requestId = null)
    {
        try
        {
            await _bridge.SendEventAsync(
                AgentThreadBridgePayload.ThreadList(_threadStore.ListThreads(), requestId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _bridge.SendEventAsync(new
            {
                type = "agent_history_error",
                requestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                code = SessionMessageCode.HistoryLoadFailed,
                detail = ex.Message
            }).ConfigureAwait(false);
        }
    }

    public async Task PublishStateAsync()
    {
        await _bridge.SendEventAsync(new
        {
            type = "agent_state",
            cwd = _thread.Cwd,
            sessionId = _thread.AcpSessionId ?? _thread.ClaudeSessionId ?? "",
            threadId = _thread.ThreadId,
            title = _thread.Title,
            status = "transcript_only",
            busy = false,
            isDraft = false,
            providerKey = _thread.Provider,
            agentName = _thread.Provider,
            assistantName = "Agent",
            supportsImage = false,
            contextUsedTokens = _thread.ContextUsedTokens,
            contextWindowTokens = _thread.ContextWindowTokens,
            contextCostAmount = _thread.ContextCostAmount,
            contextCostCurrency = _thread.ContextCostCurrency,
            store = _threadStore.RootDirectory
        }).ConfigureAwait(false);
        // A saved transcript has no live runtime; disable the toolbar Update
        // button instead of letting a click bounce off the read-only error.
        await PublishRuntimeUpdateUnavailableAsync().ConfigureAwait(false);
    }

    private Task PublishRuntimeUpdateUnavailableAsync()
    {
        return _bridge.SendEventAsync(new
        {
            type = "runtime_update_status",
            providerKey = _thread.Provider,
            state = "unavailable",
            messageCode = RuntimeStatusCode.TranscriptReadOnly,
            currentVersion = "",
            pendingVersion = "",
            // Read-only transcripts have no runtime, so no version text.
            versionLabel = "",
            versionDetail = ""
        });
    }

    public async Task RestoreAsync()
    {
        if (_disposed)
            return;

        if (ThinkingMessageNormalizer.Normalize(_thread.Messages))
            _threadStore.SaveThread(_thread);

        await _bridge.SendEventAsync(
            AgentThreadBridgePayload.ThreadLoaded(_thread, clear: true, selectPlan: false)).ConfigureAwait(false);
        await _bridge.SendEventAsync(new
        {
            type = "resume_failed",
            message = "",
            code = SessionMessageCode.ResumeUnsupportedProvider,
            args = new { provider = _thread.Provider }
        }).ConfigureAwait(false);
        await PublishStateAsync().ConfigureAwait(false);
    }

    private void OnUserMessageSubmitted(object? sender, AgentSubmitEventArgs args) =>
        _ = SubmitMessageAsync(args.Text, args.AttachmentIds);

    private void OnAttachmentUploadReceived(object? sender, AgentAttachmentUploadEventArgs args)
    {
        _ = _bridge.SendEventAsync(new
        {
            type = "agent_attachment_failed",
            clientId = args.ClientId,
            text = "",
            code = SessionMessageCode.TranscriptReadOnlyRun,
            args = (object?)null
        });
    }

    private void OnCommandReceived(object? sender, AgentCommandEventArgs args) =>
        _ = HandleCommandAsync(args);

    private async Task HandleCommandAsync(AgentCommandEventArgs args)
    {
        switch (args.Command)
        {
            case "state":
                await _bridge.SendEventAsync(
                    AgentThreadBridgePayload.ThreadLoaded(_thread, clear: true, selectPlan: false)).ConfigureAwait(false);
                await PublishStateAsync().ConfigureAwait(false);
                break;
            case "activate":
                await PublishStateAsync().ConfigureAwait(false);
                break;
            case "check_runtime_update":
                await PublishRuntimeUpdateUnavailableAsync().ConfigureAwait(false);
                break;
            case "history":
                await ListThreadsAsync(args.RequestId).ConfigureAwait(false);
                break;
            case "delete":
                _threadStore.DeleteThread(_thread.ThreadId);
                await _bridge.SendEventAsync(new { type = "command_result", text = "", code = SessionMessageCode.ThreadDeleted }).ConfigureAwait(false);
                await _bridge.SendEventAsync(new { type = "agent_workspace_close_requested" }).ConfigureAwait(false);
                break;
            case "cwd":
                if (string.IsNullOrWhiteSpace(args.Value))
                {
                    await _bridge.SendEventAsync(new
                    {
                        type = "command_result",
                        text = "",
                        code = SessionMessageCode.CwdCurrent,
                        args = new { path = _thread.Cwd }
                    }).ConfigureAwait(false);
                }
                else
                {
                    await SendReadOnlyErrorAsync().ConfigureAwait(false);
                }
                break;
            case "stop":
                await _bridge.SendEventAsync(new { type = "command_result", text = "", code = SessionMessageCode.TranscriptStopInactive }).ConfigureAwait(false);
                break;
            case "help":
                await _bridge.SendEventAsync(new
                {
                    type = "command_result",
                    text = "",
                    code = SessionMessageCode.TranscriptReadOnlyHelp
                }).ConfigureAwait(false);
                break;
            default:
                await SendReadOnlyErrorAsync().ConfigureAwait(false);
                break;
        }
    }

    private Task SendReadOnlyErrorAsync()
    {
        return _bridge.SendEventAsync(new
        {
            type = "run_failed",
            text = "",
            code = SessionMessageCode.TranscriptReadOnlyRun,
            args = (object?)null,
            runId = (string?)null,
            visionContextHintCode = ""
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _bridge.UserMessageSubmitted -= OnUserMessageSubmitted;
        _bridge.CommandReceived -= OnCommandReceived;
        _bridge.AttachmentUploadReceived -= OnAttachmentUploadReceived;
    }
}
