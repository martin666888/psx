using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

public sealed class AcpAgentSessionService : IAgentWorkspaceSession
{
    private const long MaxImageBytes = 20L * 1024 * 1024;
    private const long MaxPromptImageBytes = 50L * 1024 * 1024;
    private const int MaxPromptImages = 5;
    private static readonly HashSet<string> SupportedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif"
    };
    private sealed class AcpMode
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
    }

    private sealed class AcpConfigOption
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Category { get; init; } = "";
        public string Type { get; init; } = "";
        public string CurrentValue { get; init; } = "";
        public bool? BooleanValue { get; init; }
        public IReadOnlyList<AcpConfigOptionValue> Options { get; init; } = Array.Empty<AcpConfigOptionValue>();
    }

    private sealed class AcpConfigOptionValue
    {
        public string Value { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
    }

    private enum PermissionPresentation
    {
        Ordinary,
        Document,
        ModeTransition
    }

    private sealed class PendingPermission
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AgentDecisionOption> Options { get; init; } = Array.Empty<AgentDecisionOption>();
        public PermissionPresentation Presentation { get; init; }
        public string DecisionSnapshotId { get; init; } = "";
        /// <summary>
        /// When set, this permission is the ask-user form variant: the frontend
        /// renders an elicitation-shaped schema and may reply with JSON
        /// <c>{ optionId, content }</c> instead of a bare optionId.
        /// </summary>
        public bool IsAskUserForm { get; init; }
        public IReadOnlyList<int> AskUserAnswerIndexes { get; init; } = Array.Empty<int>();
        public string? FormSubmitOptionId { get; init; }
        public bool IsDocumentDecision => Presentation is PermissionPresentation.Document or PermissionPresentation.ModeTransition;
        public bool IsModeTransition => Presentation == PermissionPresentation.ModeTransition;
    }

    private sealed class PendingElicitation
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A standard ACP authentication method as advertised by the agent in the
    /// <c>initialize</c> result's <c>authMethods</c> array. Parsed generically
    /// (type/args/env are read from the payload) — no provider-name branching.
    /// </summary>
    private sealed class AcpAuthMethod
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Raised when an ACP request fails with the authentication-required code
    /// (<see cref="AcpJsonRpcException.AuthRequiredCode"/>) and PSX could not
    /// silently re-authenticate. Callers must treat this as a recoverable
    /// <c>auth_required</c> state (the user can log in and retry), never as a
    /// permanent <c>transcript_only</c> fallback.
    /// </summary>
    private sealed class AcpAuthRequiredException : Exception
    {
        public AcpAuthRequiredException(string message) : base(message) { }
    }

    private sealed class AcpTerminalProcess
    {
        public Process Process { get; init; } = null!;
        public StringBuilder Output { get; } = new();
        public int OutputByteCount { get; set; }
        public int OutputByteLimit { get; init; } = 200_000;
        public bool Truncated { get; set; }
        public long TransportGeneration { get; init; }
        public CancellationToken TransportToken { get; init; }
    }

    private sealed class ReplayHistoryState
    {
        public List<AgentMessage> Messages { get; } = new();
        public StringBuilder ThinkingBuffer { get; } = new();
        public StringBuilder AssistantBuffer { get; } = new();
        public Dictionary<string, string> ToolNames { get; } = new();
        public Dictionary<string, string> ToolInputs { get; } = new();
        public Dictionary<string, string> ToolOutputs { get; } = new();
        public Dictionary<string, string> ToolSummaries { get; } = new();
        public Dictionary<string, string> ToolRunIds { get; } = new();
        public Dictionary<string, string> ToolStatuses { get; } = new();
        public List<string> ToolOrder { get; } = new();
        public string? CurrentRunId { get; set; }
        public int TurnIndex { get; set; }
    }

    private readonly IAgentBridgeService _bridgeService;
    private readonly ITabManagementService _tabManagementService;
    private readonly ITerminalBridgeService _terminalBridgeService;
    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentDirectoryPicker _directoryPicker;
    private readonly IAgentProviderRegistry _providerRegistry;
    private readonly IAcpAgentProvider _provider;
    private readonly IAcpAgentRuntime _runtime;
    private readonly IAgentRuntimeCoordinator _runtimeCoordinator;
    private readonly object _runLock = new();
    private readonly object _sessionMutationSync = new();
    private readonly object _commandLock = new();
    private readonly object _runtimeInstallLock = new();
    private readonly SemaphoreSlim _transportLock = new(1, 1);
    private readonly SemaphoreSlim _sessionRestoreLock = new(1, 1);
    private readonly CancellationTokenSource _serviceLifetimeCts = new();
    private readonly ConcurrentDictionary<string, PendingPermission> _pendingPermissions = new();
    private readonly ConcurrentDictionary<string, PendingElicitation> _pendingElicitations = new();
    private readonly ConcurrentDictionary<string, AcpTerminalProcess> _terminals = new();
    private readonly StringBuilder _thinkingBuffer = new();
    private readonly StringBuilder _assistantBuffer = new();
    private readonly AcpToolStateTracker _toolState = new();
    private readonly Dictionary<string, string> _availableAgentCommands = new(StringComparer.OrdinalIgnoreCase);
    private AgentThread _currentThread;
    private AcpJsonRpcTransport? _transport;
    private string _workingDirectory = "";
    private string? _acpSessionId;
    private string? _sessionIdForTransportRecovery;
    private string? _adapterVersion;
    private string _status = "ready";
    private string? _currentRunId;
    private IReadOnlyList<AcpMode> _modes = Array.Empty<AcpMode>();
    private IReadOnlyList<AcpConfigOption> _configOptions = Array.Empty<AcpConfigOption>();
    private string? _currentModeId;
    private bool _isRunning;
    private SessionRestoreMode _restoreMode;
    private bool _supportsImage = true;
    private bool _supportsSessionResume;
    private ReplayHistoryState? _replayHistory;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _runRequestCts;
    private Task? _currentRunTask;
    private AcpJsonRpcTransport? _currentRunTransport;
    private long _currentRunTransportGeneration;
    private CancellationTokenSource? _transportLifetimeCts;
    private long _transportGeneration;
    private bool _transportRecoveryRequired;
    private bool _agentCommandsReady;
    private CancellationTokenSource? _runtimeInstallCts;
    private bool _runtimeInstallInProgress;
    private string _runtimeInstallState = "missing";
    private string _runtimeInstallMessage = "Agent runtime is not installed.";
    private bool _restoreBlockedByRuntime;
    private bool _disposed;
    private IReadOnlyList<AcpAuthMethod> _authMethods = Array.Empty<AcpAuthMethod>();

    // Absolute wall-clock timeout for the session/prompt request. Null means the
    // request only completes on result, cancellation, or a real transport
    // disconnect (SignalDisconnected -> CompletePendingWithError faults the
    // pending request). A finite timeout here would kill long multi-agent turns
    // mid-flight. Exposed as a test seam so tests can assert it is null while
    // control operations keep their finite timeouts.
    internal static readonly TimeSpan? PromptRequestTimeout = null;

    // Grace period the cancel path waits for the adapter's session/cancel before
    // force-resetting the transport. Injectable so tests need not wait the full
    // wall-clock duration.
    internal TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(5);

    // Short budget for the session/cancel stdin write. The writer pump makes the
    // write non-blocking; this budget guarantees the stop watchdog never waits on
    // a stalled pipe or an agent that stopped acknowledging cancel.
    internal TimeSpan StopCancelWriteBudget = TimeSpan.FromSeconds(1);

    // Bounded wait to acquire the transport lock during a forced reset or dispose.
    // If a stalled operation holds it, we hard-stop by disposing the transport
    // directly so stop/close always completes in a determinate time.
    internal TimeSpan TransportResetLockBudget = TimeSpan.FromSeconds(2);

    internal AcpAgentSessionService(
        Guid workspaceId,
        IAgentBridgeService bridgeService,
        ITabManagementService tabManagementService,
        ITerminalBridgeService terminalBridgeService,
        IAgentThreadStore threadStore,
        IAgentDirectoryPicker directoryPicker,
        IAgentProviderRegistry providerRegistry,
        IAcpAgentProvider provider,
        AgentThread initialThread,
        IAgentRuntimeCoordinator runtimeCoordinator)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Agent Workspace ID must not be empty.", nameof(workspaceId));

        WorkspaceId = workspaceId;
        _bridgeService = bridgeService;
        _tabManagementService = tabManagementService;
        _terminalBridgeService = terminalBridgeService;
        _threadStore = threadStore;
        _directoryPicker = directoryPicker;
        _providerRegistry = providerRegistry;
        _provider = provider;
        _runtime = _provider.Runtime;
        _runtimeCoordinator = runtimeCoordinator;
        if (IsAgentRuntimeReady())
        {
            _runtimeInstallState = ResolveReadyRuntimeState();
            _runtimeInstallMessage = "Agent runtime is ready.";
        }
        _currentThread = initialThread;
        if (IsEmptyAgentDraft(_currentThread))
            _currentThread.Provider = _provider.Descriptor.Key;
        ApplyThread(_currentThread);
        _bridgeService.UserMessageSubmitted += OnUserMessageSubmitted;
        _bridgeService.CommandReceived += OnCommandReceived;
        _bridgeService.AttachmentUploadReceived += OnAttachmentUploadReceived;
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _runtimeCoordinator.UpdateStatusChanged += OnRuntimeUpdateStatusChanged;
    }

    public Guid WorkspaceId { get; }
    public string ThreadId => _currentThread.ThreadId;
    public string ProviderKey => _provider.Descriptor.Key;
    public string WorkingDirectory => _workingDirectory;
    public bool IsDraft => IsEmptyAgentDraft(_currentThread);

    public async Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null)
    {
        if (_disposed)
            return;

        var trimmed = text.Trim();
        var attachments = attachmentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        if (TryParseLeadingSlashCommand(trimmed, out var commandName, out var arguments))
        {
            if (attachments.Length > 0)
            {
                await SendCommandRejectedAsync(commandName, "attachments_not_allowed").ConfigureAwait(false);
                return;
            }

            if (TryHandlePsxSlashCommand(trimmed, out var commandTask))
            {
                await commandTask.ConfigureAwait(false);
                return;
            }

            if (!TryResolveAgentCommand(commandName, out var canonicalName, out var commandsReady))
            {
                await SendCommandRejectedAsync(
                    commandName,
                    commandsReady ? "unsupported" : "commands_loading").ConfigureAwait(false);
                return;
            }

            trimmed = string.IsNullOrWhiteSpace(arguments)
                ? canonicalName
                : canonicalName + " " + arguments;
        }

        await StartAcpRunAsync(trimmed, attachments).ConfigureAwait(false);
    }

    public async Task CancelAsync()
    {
        await CancelRunAsync(notify: true).ConfigureAwait(false);
    }

    private async Task CancelRunAsync(bool notify)
    {
        CancellationTokenSource? cts;
        CancellationTokenSource? requestCts;
        Task? oldTask;
        AcpJsonRpcTransport? runTransport;
        long runTransportGeneration;
        string? stoppingRunId;
        bool hadRun;
        lock (_runLock)
        {
            cts = _runCts;
            requestCts = _runRequestCts;
            oldTask = _currentRunTask;
            // Capture the run we are stopping so the tail cleanup below can
            // tell it apart from a newer run the user may have started while
            // we waited for the old task to finish.
            stoppingRunId = _currentRunId;
            hadRun = stoppingRunId != null || cts != null || requestCts != null || oldTask != null;
            runTransport = _currentRunTransport ?? _transport;
            runTransportGeneration = _currentRunTransportGeneration != 0
                ? _currentRunTransportGeneration
                : _transportGeneration;
        }

        // Mark the run cancelled immediately, but let the in-flight prompt
        // request wait briefly for the adapter's session/cancel handling.
        try { cts?.Cancel(); } catch { }

        // Publish "stopping" right away so the UI reacts instantly, before any
        // transport I/O. This must never depend on the agent acknowledging cancel.
        if (notify && hadRun)
        {
            lock (_runLock)
            {
                if (_currentRunId == stoppingRunId && ReferenceEquals(_runCts, cts))
                    _status = "stopping";
            }
            await PublishStateAsync().ConfigureAwait(false);
        }

        var sessionId = _acpSessionId;
        var notificationTransport = runTransport ?? _transport;
        if (oldTask != null && !string.IsNullOrWhiteSpace(sessionId) && notificationTransport != null)
        {
            try
            {
                // Best-effort cancel with a short write budget. The writer pump
                // makes this non-blocking, and the budget ensures a stalled pipe
                // (or an agent that stopped reading stdin) cannot delay the
                // watchdog below.
                await notificationTransport
                    .SendNotificationAsync("session/cancel", new { sessionId })
                    .WaitAsync(StopCancelWriteBudget)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        foreach (var item in _pendingPermissions.ToArray())
        {
            if (_pendingPermissions.TryRemove(item.Key, out var pending))
            {
                pending.Completion.TrySetResult("__cancelled__");
                if (pending.IsDocumentDecision)
                    UpdateDocumentDecisionState(item.Key, pending.Presentation, "cancelled", decisionSnapshotId: pending.DecisionSnapshotId);
                await _bridgeService.SendEventAsync(new
                {
                    type = "permission_cancelled",
                    requestId = item.Key,
                    text = "Request cancelled because the current run stopped."
                }).ConfigureAwait(false);
            }
        }

        foreach (var item in _pendingElicitations.ToArray())
        {
            if (_pendingElicitations.TryRemove(item.Key, out var pending))
            {
                pending.Completion.TrySetResult("{\"action\":\"cancel\"}");
                await _bridgeService.SendEventAsync(new
                {
                    type = "elicitation_cancelled",
                    requestId = item.Key,
                    text = "Input request cancelled because the current run stopped."
                }).ConfigureAwait(false);
            }
        }

        var forceReset = false;
        if (oldTask != null)
        {
            try
            {
                await oldTask.WaitAsync(CancelGracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                forceReset = true;
                try { requestCts?.Cancel(); } catch { }
            }
            catch
            {
                // The old task is already complete or faulted; its finally
                // block owns the remaining run cleanup.
            }
        }

        if (forceReset && runTransport != null)
        {
            await ResetTransportAsync(runTransport, runTransportGeneration).ConfigureAwait(false);
            try
            {
                await oldTask!.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Resetting the transport is the hard stop. The run-id guard
                // prevents a late task from mutating a newer run.
            }
        }

        var ownsStoppingRun = false;
        lock (_runLock)
        {
            ownsStoppingRun = hadRun
                && _currentRunId == stoppingRunId
                && ReferenceEquals(_runCts, cts);
        }
        if (ownsStoppingRun)
        {
            await FinalizeRunOutputAsync(stoppingRunId, cts).ConfigureAwait(false);
        }

        lock (_runLock)
        {
            // Only reset run state if the run we were cancelling is still the
            // current one. If a newer run started while we awaited the old
            // task, its id/cts/task must be left intact (otherwise the new
            // run becomes uncancellable because _runCts would be nulled).
            if (hadRun
                && _currentRunId == stoppingRunId
                && ReferenceEquals(_runCts, cts))
            {
                _isRunning = false;
                // A forced cancel resets (kills) the transport, so the next
                // prompt must reload the session. Report that instead of a
                // misleading "ready".
                _status = _transportRecoveryRequired ? "recovery_pending" : "ready";
                _currentRunId = null;
                _runCts = null;
                _runRequestCts = null;
                _currentRunTask = null;
                _currentRunTransport = null;
                _currentRunTransportGeneration = 0;
            }
            else if (_transportRecoveryRequired
                && _currentRunId == null
                && _status != "transcript_only")
            {
                // The run's own finally already cleared its state while we were
                // force-resetting the transport, and may have written "ready"
                // before the reset flag was set. Force the recovery status so the
                // UI does not claim the workspace is ready when the next prompt
                // must reload the session.
                _status = "recovery_pending";
            }
        }

        if (notify)
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "command_result",
                text = hadRun ? "ACP run stopped." : "No ACP run is currently active."
            }).ConfigureAwait(false);
        }
        await PublishStateAsync().ConfigureAwait(false);
    }

    public async Task RestoreAsync()
    {
        if (_disposed)
            return;

        var token = _serviceLifetimeCts.Token;
        await _sessionRestoreLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RestoreCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sessionRestoreLock.Release();
        }
    }

    public async Task OnRuntimeReadyAsync()
    {
        if (_disposed)
            return;

        // Every workspace owns a frontend runtime slice even when providers
        // share one runtime. Refresh it immediately so stale install cards and
        // disabled composers disappear in all matching tabs.
        await PublishRuntimeStatusAsync().ConfigureAwait(false);

        var token = _serviceLifetimeCts.Token;
        await _sessionRestoreLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed || !_restoreBlockedByRuntime || !IsAgentRuntimeReady())
                return;

            await RestoreCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sessionRestoreLock.Release();
        }
    }

    private async Task RestoreCoreAsync(CancellationToken token)
    {
        await CancelRunAsync(notify: false).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        try
        {
            lock (_sessionMutationSync)
            {
                if (ThinkingMessageNormalizer.Normalize(_currentThread.Messages))
                    _threadStore.SaveThread(_currentThread);
            }
            ApplyThread(_currentThread);
            await SendAgentCommandsUnavailableAsync().ConfigureAwait(false);
            _acpSessionId = null;
            _threadStore.SaveLastThread(_currentThread);
            await SendThreadLoadedAsync(clear: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Preparation failures (thread store IO, normalization, snapshot
            // send) must degrade to the local transcript, never kill the
            // restore — the workspace is already created and activated.
            _restoreBlockedByRuntime = false;
            _status = "transcript_only";
            await SendResumeFailedAsync(
                "PSX could not prepare the Agent session. The saved local transcript is still available to read.",
                ex.Message).ConfigureAwait(false);
            await PublishStateAsync().ConfigureAwait(false);
            return;
        }

        if (!IsBoundProviderThread(_currentThread))
        {
            _restoreBlockedByRuntime = false;
            _status = "transcript_only";
            await SendResumeFailedAsync(BuildUnsupportedProviderMessage(_currentThread.Provider)).ConfigureAwait(false);
            await PublishStateAsync().ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentThread.AcpSessionId))
        {
            _restoreBlockedByRuntime = false;
            _status = "transcript_only";
            await SendResumeFailedAsync("This thread has no ACP session ID. The saved local transcript is still available to read.").ConfigureAwait(false);
            await PublishStateAsync().ConfigureAwait(false);
            return;
        }

        if (!IsAgentRuntimeReady())
        {
            _restoreBlockedByRuntime = true;
            _status = "transcript_only";
            await SendResumeFailedAsync(
                "Install the Agent runtime to continue this saved session. PSX will retry automatically when the runtime is ready.").ConfigureAwait(false);
            await PublishStateAsync().ConfigureAwait(false);

            // Installation may finish between the readiness check above and
            // the coordinator notification observing this blocked flag. Close
            // that race locally instead of waiting for another status event
            // which may never arrive.
            if (!IsAgentRuntimeReady())
                return;
        }

        _restoreBlockedByRuntime = false;

        // Publish the restoring state before the (slow) transport startup and
        // initialize: the workspace is already visible and should show
        // progress instead of looking dead while ACP spins up.
        _status = "restoring";
        await PublishStateAsync().ConfigureAwait(false);

        try
        {
            var restored = await LoadAcpHistoryAsync(_currentThread.AcpSessionId, token).ConfigureAwait(false);
            if (restored)
            {
                _restoreBlockedByRuntime = false;
                _status = "restored";
                // Session context restore is workspace status, not conversation
                // content: it reaches the UI through agent_state only.
            }
            else
            {
                _restoreBlockedByRuntime = false;
                _status = "transcript_only";
                await SendResumeFailedAsync("The Agent session loaded, but its transcript could not be replayed. The saved local transcript is still available to read.").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (AcpAuthRequiredException ex)
        {
            // Login required to restore this session. Keep it recoverable: the
            // user can log in and retry, so do NOT fall back to transcript_only.
            _restoreBlockedByRuntime = false;
            _status = "auth_required";
            await SendResumeFailedAsync(ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _acpSessionId = null;
            _restoreBlockedByRuntime = !IsAgentRuntimeReady();
            _status = "transcript_only";
            await SendResumeFailedAsync(
                _restoreBlockedByRuntime
                    ? "The Agent runtime became unavailable while restoring this session. PSX will retry automatically when it is ready."
                    : "PSX could not restore the Agent session. The saved local transcript is still available to read.",
                ex.Message).ConfigureAwait(false);
        }

        await PublishStateAsync().ConfigureAwait(false);
    }

    public async Task DeleteThreadAsync(string threadId)
    {
        if (_disposed || !string.Equals(threadId, _currentThread.ThreadId, StringComparison.OrdinalIgnoreCase))
            return;

        await CancelRunAsync(notify: false).ConfigureAwait(false);
        var token = _serviceLifetimeCts.Token;

        if (!string.IsNullOrWhiteSpace(_acpSessionId) && _transport != null)
        {
            try
            {
                await _transport.SendRequestAsync(
                        "session/delete",
                        new { sessionId = _acpSessionId },
                        TimeSpan.FromSeconds(15),
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        _threadStore.DeleteThread(threadId);
        await _bridgeService.SendEventAsync(new { type = "command_result", text = "Deleted thread." }).ConfigureAwait(false);
        await _bridgeService.SendEventAsync(new { type = "agent_workspace_close_requested" }).ConfigureAwait(false);
    }

    public async Task ChangeDirectoryAsync(string path)
    {
        if (!IsEmptyAgentDraft(_currentThread))
        {
            await SendRunFailedAsync(
                "The working directory is locked after the first message. Create a new Agent tab to use another directory.")
                .ConfigureAwait(false);
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        var fullPath = Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(_workingDirectory, expanded));

        if (!Directory.Exists(fullPath))
        {
            await SendRunFailedAsync($"Directory does not exist: {fullPath}").ConfigureAwait(false);
            return;
        }

        lock (_sessionMutationSync)
        {
            _workingDirectory = fullPath;
            _currentThread.Cwd = fullPath;
            SaveCurrentThreadCore();
        }
        await SendThreadLoadedAsync(clear: true, selectPlan: true).ConfigureAwait(false);
        await _bridgeService.SendEventAsync(new { type = "command_result", text = $"Working directory changed to: {fullPath}" }).ConfigureAwait(false);
        await PublishStateAsync().ConfigureAwait(false);
    }

    public async Task ListThreadsAsync(string? requestId = null)
    {
        try
        {
            await _bridgeService.SendEventAsync(
                AgentThreadBridgePayload.ThreadList(_threadStore.ListThreads(), requestId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendHistoryErrorAsync(ex, requestId).ConfigureAwait(false);
        }
    }

    private Task SendHistoryErrorAsync(Exception exception, string? requestId = null)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_history_error",
            requestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId,
            text = $"Unable to load Agent thread history. {exception.Message}"
        });
    }

    public async Task PublishStateAsync()
    {
        var threadProvider = _providerRegistry.Find(_currentThread.Provider);
        await _bridgeService.SendEventAsync(new
        {
            type = "agent_state",
            cwd = _workingDirectory,
            sessionId = _acpSessionId ?? _sessionIdForTransportRecovery ?? _currentThread.AcpSessionId ?? "",
            threadId = _currentThread.ThreadId,
            title = _currentThread.Title,
            status = _status,
            busy = _isRunning,
            isDraft = IsDraft,
            providerKey = _currentThread.Provider,
            agentName = threadProvider?.Descriptor.DisplayName ?? _currentThread.Provider,
            assistantName = threadProvider?.Descriptor.AssistantName ?? "Agent",
            supportsImage = _supportsImage,
            contextUsedTokens = _currentThread.ContextUsedTokens,
            contextWindowTokens = _currentThread.ContextWindowTokens,
            contextCostAmount = _currentThread.ContextCostAmount,
            contextCostCurrency = _currentThread.ContextCostCurrency,
            store = _threadStore.RootDirectory
        }).ConfigureAwait(false);
        if (IsBoundProviderThread(_currentThread))
        {
            await PublishRuntimeStatusAsync().ConfigureAwait(false);
            await PublishRuntimeUpdateSnapshotAsync().ConfigureAwait(false);
        }
    }

    private void OnUserMessageSubmitted(object? sender, AgentSubmitEventArgs e)
    {
        _ = SubmitMessageAsync(e.Text, e.AttachmentIds);
    }

    private void OnAttachmentUploadReceived(object? sender, AgentAttachmentUploadEventArgs e)
    {
        _ = UploadAttachmentAsync(e);
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
                await SendThreadLoadedAsync(clear: true).ConfigureAwait(false);
                await PublishStateAsync().ConfigureAwait(false);
                break;
            case "activate":
                await ActivateAsync().ConfigureAwait(false);
                break;
            case "install_runtime":
                await InstallRuntimeAsync().ConfigureAwait(false);
                break;
            case "cancel_runtime_install":
                await CancelRuntimeInstallAsync().ConfigureAwait(false);
                break;
            case "check_runtime_update":
                await CheckRuntimeUpdateAsync().ConfigureAwait(false);
                break;
            case "cwd":
                if (string.IsNullOrWhiteSpace(e.Value))
                    await _bridgeService.SendEventAsync(new { type = "command_result", text = $"Current working directory: {_workingDirectory}" }).ConfigureAwait(false);
                else
                    await ChangeDirectoryAsync(e.Value).ConfigureAwait(false);
                break;
            case "pick_cwd":
                await PickDirectoryAsync().ConfigureAwait(false);
                break;
            case "terminal":
                await OpenNativeAgentTerminalAsync().ConfigureAwait(false);
                break;
            case "stop":
                await CancelAsync().ConfigureAwait(false);
                break;
            case "history":
                await ListThreadsAsync(e.RequestId).ConfigureAwait(false);
                break;
            case "delete":
                await DeleteThreadAsync(_currentThread.ThreadId).ConfigureAwait(false);
                break;
            case "help":
                await SendHelpAsync().ConfigureAwait(false);
                break;
            case "agent_command":
            case "claude_command":
                if (!string.IsNullOrWhiteSpace(e.Value))
                    await SubmitMessageAsync(e.Value).ConfigureAwait(false);
                break;
            case "set_mode":
                if (!string.IsNullOrWhiteSpace(e.Value))
                    await SetModeAsync(e.Value).ConfigureAwait(false);
                break;
            case "set_config_option":
                if (!string.IsNullOrWhiteSpace(e.RequestId)
                    && (e.BooleanValue.HasValue || !string.IsNullOrWhiteSpace(e.Value)))
                {
                    await SetConfigOptionAsync(
                        e.RequestId,
                        e.BooleanValue.HasValue ? e.BooleanValue.Value : e.Value!,
                        e.BooleanValue.HasValue).ConfigureAwait(false);
                }
                break;
            case "agent_permission_response":
                if (!string.IsNullOrWhiteSpace(e.RequestId)
                    && _pendingPermissions.TryRemove(e.RequestId, out var pending))
                {
                    pending.Completion.TrySetResult(e.Value ?? "");
                }
                break;
            case "agent_elicitation_response":
                if (!string.IsNullOrWhiteSpace(e.RequestId)
                    && _pendingElicitations.TryRemove(e.RequestId, out var elicitation))
                {
                    elicitation.Completion.TrySetResult(e.Value ?? "{\"action\":\"cancel\"}");
                }
                break;
        }
    }

    private async Task ActivateAsync()
    {
        await PublishStateAsync().ConfigureAwait(false);
    }

    private bool IsAgentRuntimeReady()
    {
        return _runtime.IsReady();
    }

    private void OnRuntimeStatusChanged(string message)
    {
        lock (_runtimeInstallLock)
        {
            if (!_runtimeInstallInProgress)
                return;

            _runtimeInstallMessage = message;
        }

        _ = PublishRuntimeStatusAsync();
    }

    private async Task InstallRuntimeAsync()
    {
        CancellationTokenSource? installCts;
        lock (_runtimeInstallLock)
        {
            if (_runtimeInstallInProgress)
                return;

            if (IsAgentRuntimeReady())
            {
                _runtimeInstallState = ResolveReadyRuntimeState();
                _runtimeInstallMessage = "Agent runtime is ready.";
                installCts = null;
            }
            else
            {
                _runtimeInstallInProgress = true;
                _runtimeInstallState = "installing";
                _runtimeInstallMessage = "Preparing to install the Agent runtime...";
                _runtimeInstallCts = new CancellationTokenSource();
                installCts = _runtimeInstallCts;
            }
        }

        if (installCts == null)
        {
            await PublishRuntimeStatusAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await PublishRuntimeStatusAsync().ConfigureAwait(false);

            var result = await _runtime.EnsureInstalledAsync(installCts.Token).ConfigureAwait(false);

            lock (_runtimeInstallLock)
            {
                switch (result.Kind)
                {
                    case AcpRuntimeOperationKind.Success:
                    case AcpRuntimeOperationKind.AlreadyReady:
                        _runtimeInstallState = ResolveReadyRuntimeState();
                        _runtimeInstallMessage = "Agent runtime installed successfully.";
                        break;
                    case AcpRuntimeOperationKind.Cancelled:
                        _runtimeInstallState = "cancelled";
                        _runtimeInstallMessage = "Agent runtime installation was cancelled.";
                        break;
                    case AcpRuntimeOperationKind.NetworkUnavailable:
                        _runtimeInstallState = "failed";
                        _runtimeInstallMessage = "Download failed. Check the network connection and retry.";
                        break;
                    default:
                        _runtimeInstallState = "failed";
                        _runtimeInstallMessage = $"Agent runtime installation failed. {result.Message}";
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_runtimeInstallLock)
            {
                _runtimeInstallState = "cancelled";
                _runtimeInstallMessage = "Agent runtime installation was cancelled.";
            }
        }
        finally
        {
            lock (_runtimeInstallLock)
            {
                _runtimeInstallInProgress = false;
                _runtimeInstallCts?.Dispose();
                _runtimeInstallCts = null;
            }

            await PublishRuntimeStatusAsync().ConfigureAwait(false);
        }
    }

    private async Task CancelRuntimeInstallAsync()
    {
        CancellationTokenSource? installCts;
        lock (_runtimeInstallLock)
        {
            installCts = _runtimeInstallCts;
            if (!_runtimeInstallInProgress || installCts == null)
                return;

            _runtimeInstallMessage = "Cancelling Agent runtime installation...";
        }

        try { installCts.Cancel(); } catch { }
        await PublishRuntimeStatusAsync().ConfigureAwait(false);
    }

    private async Task PublishRuntimeStatusAsync()
    {
        string state;
        string message;
        bool canCancel;

        lock (_runtimeInstallLock)
        {
            if (!_runtimeInstallInProgress && IsAgentRuntimeReady())
            {
                _runtimeInstallState = ResolveReadyRuntimeState();
                _runtimeInstallMessage = "Agent runtime is ready.";
            }
            else if (_runtimeInstallState is "ready")
            {
                _runtimeInstallState = "missing";
                _runtimeInstallMessage = "Agent runtime is not installed.";
            }

            state = _runtimeInstallState;
            message = _runtimeInstallMessage;
            canCancel = _runtimeInstallInProgress;
        }

        await _bridgeService.SendEventAsync(new
        {
            type = "runtime_status",
            providerKey = _provider.Descriptor.Key,
            agentName = _provider.Descriptor.DisplayName,
            state,
            message,
            canInstall = state is "missing" or "failed" or "cancelled",
            canCancel,
            ownership = "managed",
            canGuide = false
        }).ConfigureAwait(false);
    }

    private static string ResolveReadyRuntimeState() => "ready";

    /// <summary>
    /// User-requested update check. The runtime coordinator single-flights
    /// concurrent requests per runtime and broadcasts every lifecycle state,
    /// so all tabs sharing one runtime stay in sync and trigger at most one
    /// npm run.
    /// </summary>
    private async Task CheckRuntimeUpdateAsync()
    {
        if (!_runtime.SupportsSelfUpdate)
        {
            await PublishRuntimeUpdateStatusAsync("unsupported").ConfigureAwait(false);
            return;
        }

        // A missing runtime cannot be refreshed: AcpRuntimeManager.RefreshAsync
        // reports AlreadyReady ("skipping") in that case, which must not be
        // presented as "up to date". Install comes first.
        if (!IsAgentRuntimeReady())
        {
            await PublishRuntimeUpdateStatusAsync("install_required").ConfigureAwait(false);
            return;
        }

        // Progress and outcome reach this workspace (and every other one
        // sharing the runtime) through OnRuntimeUpdateStatusChanged; failures
        // are converted into a "failed" broadcast by the coordinator.
        await _runtimeCoordinator.RequestUpdateAsync(_runtime).ConfigureAwait(false);
    }

    private void OnRuntimeUpdateStatusChanged(object? sender, RuntimeUpdateStatusChangedEventArgs args)
    {
        if (!ReferenceEquals(args.Runtime, _runtime) || _disposed)
            return;
        _ = PublishRuntimeUpdateStatusAsync(args.State, args.Message);
    }

    /// <summary>
    /// Startup/state snapshot for the toolbar Update button: unsupported for
    /// bundled runtimes, install-required before the runtime exists, then the
    /// live lifecycle (checking / staged), then the coordinator's process-wide
    /// outcome snapshot so a workspace created or re-activated after an update
    /// finished still shows failed / up_to_date, idle otherwise.
    /// </summary>
    private Task PublishRuntimeUpdateSnapshotAsync()
    {
        if (!_runtime.SupportsSelfUpdate)
            return PublishRuntimeUpdateStatusAsync("unsupported");
        if (!IsAgentRuntimeReady())
            return PublishRuntimeUpdateStatusAsync("install_required");
        if (_runtimeCoordinator.IsUpdateInFlight(_runtime))
            return PublishRuntimeUpdateStatusAsync("checking");
        if (_runtime.GetVersionSnapshot().HasPendingUpdate)
            return PublishRuntimeUpdateStatusAsync("staged_restart_required");

        var snapshot = _runtimeCoordinator.GetUpdateSnapshot(_runtime);
        if (snapshot is { State: "failed" or "up_to_date" })
            return PublishRuntimeUpdateStatusAsync(snapshot.State, snapshot.Message);

        return PublishRuntimeUpdateStatusAsync("idle");
    }

    private Task PublishRuntimeUpdateStatusAsync(string state, string? message = null)
    {
        var snapshot = _runtime.GetVersionSnapshot();
        return _bridgeService.SendEventAsync(new
        {
            type = "runtime_update_status",
            providerKey = _provider.Descriptor.Key,
            state,
            message = message ?? "",
            currentVersion = snapshot.CurrentVersion ?? "",
            pendingVersion = snapshot.PendingVersion ?? "",
            // User-recognizable product version for the toolbar label; empty
            // means "render no version text" (never a misleading v0/Unknown).
            versionLabel = BuildVersionLabel(snapshot),
            versionDetail = snapshot.TechnicalDetails ?? ""
        });
    }

    private static string BuildVersionLabel(RuntimeVersionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.CurrentVersion))
            return "";
        return string.IsNullOrWhiteSpace(snapshot.ProductName)
            ? $"v{snapshot.CurrentVersion}"
            : $"{snapshot.ProductName} v{snapshot.CurrentVersion}";
    }

    private bool TryHandlePsxSlashCommand(string commandText, out Task task)
    {
        if (!TryParseLeadingSlashCommand(commandText, out var commandName, out var value))
        {
            task = Task.CompletedTask;
            return false;
        }

        var command = commandName.ToLowerInvariant();

        task = command switch
        {
            "/cwd" => string.IsNullOrWhiteSpace(value)
                ? _bridgeService.SendEventAsync(new { type = "command_result", text = $"Current working directory: {_workingDirectory}" })
                : ChangeDirectoryAsync(value),
            "/terminal" => OpenNativeAgentTerminalAsync(),
            "/stop" => CancelAsync(),
            "/history" => ListThreadsAsync(),
            "/delete" => DeleteThreadAsync(_currentThread.ThreadId),
            "/help" => SendHelpAsync(),
            _ => Task.CompletedTask
        };

        return command is "/cwd" or "/terminal" or "/stop" or "/history" or "/delete" or "/help";
    }

    private static bool TryParseLeadingSlashCommand(
        string text,
        out string commandName,
        out string arguments)
    {
        commandName = "";
        arguments = "";
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
            return false;

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator < 0)
        {
            commandName = text;
            return true;
        }

        commandName = text[..separator];
        arguments = text[(separator + 1)..].TrimStart();
        return true;
    }

    private bool TryResolveAgentCommand(
        string commandName,
        out string canonicalName,
        out bool commandsReady)
    {
        lock (_commandLock)
        {
            commandsReady = _agentCommandsReady;
            return _availableAgentCommands.TryGetValue(commandName, out canonicalName!);
        }
    }

    private Task SendCommandRejectedAsync(string commandName, string reason)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_command_rejected",
            command = commandName,
            reason
        });
    }

    private void ClearAvailableAgentCommands()
    {
        lock (_commandLock)
        {
            _availableAgentCommands.Clear();
            _agentCommandsReady = false;
        }
    }

    private Task SendAgentCommandsUnavailableAsync()
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_commands",
            ready = false,
            commands = Array.Empty<object>()
        });
    }

    private async Task PickDirectoryAsync()
    {
        var selected = _directoryPicker.PickDirectory(_workingDirectory);
        if (!string.IsNullOrWhiteSpace(selected))
            await ChangeDirectoryAsync(selected).ConfigureAwait(false);
    }

    private Task SendHelpAsync()
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "command_result",
            text = $"PSX commands: /cwd, /cwd <path>, /terminal, /stop, /history, /delete, /help. ACP commands are sent through {_provider.Descriptor.AssistantName} Agent."
        });
    }

    private async Task UploadAttachmentAsync(AgentAttachmentUploadEventArgs e)
    {
        try
        {
            await EnsureTransportAsync().ConfigureAwait(false);

            if (!_supportsImage)
                throw new InvalidOperationException("Current ACP Agent does not support image input.");

            if (!SupportedImageMimeTypes.Contains(e.MimeType))
                throw new InvalidOperationException("Only PNG, JPEG, WebP, and GIF images are supported.");

            if (e.Size <= 0 || e.Size > MaxImageBytes)
                throw new InvalidOperationException("Each image must be 20MB or smaller.");

            byte[] data;
            try
            {
                data = Convert.FromBase64String(e.DataBase64);
            }
            catch
            {
                throw new InvalidOperationException("Image upload data is invalid.");
            }

            if (data.LongLength != e.Size)
                throw new InvalidOperationException("Image upload size did not match the file metadata.");

            var attachment = _threadStore.SaveAttachment(_currentThread.ThreadId, e.FileName, e.MimeType, data);
            await _bridgeService.SendEventAsync(new
            {
                type = "agent_attachment_uploaded",
                clientId = e.ClientId,
                attachment = ToAttachmentPayload(attachment)
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "agent_attachment_failed",
                clientId = e.ClientId,
                text = ex.Message
            }).ConfigureAwait(false);
        }
    }

    private async Task OpenNativeAgentTerminalAsync()
    {
        var profile = _provider.CreateNativeTerminalProfile(_workingDirectory, _acpSessionId);
        if (profile == null)
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "command_result",
                text = $"{_provider.Descriptor.DisplayName} does not provide a native Terminal entry point."
            }).ConfigureAwait(false);
            return;
        }

        await _tabManagementService.CreateTabAsync(profile).ConfigureAwait(false);
        await _terminalBridgeService.SetViewModeAsync("terminal").ConfigureAwait(false);
        await _bridgeService.SendEventAsync(new
        {
            type = "raw_terminal_fallback",
            text = $"Opened a raw {_provider.Descriptor.DisplayName} terminal tab in the current working directory."
        }).ConfigureAwait(false);
        _status = "fallback";
        await PublishStateAsync().ConfigureAwait(false);
    }

    // ---- Standard ACP terminal-auth login ----

    private static IReadOnlyList<AcpAuthMethod> ParseAuthMethods(JsonElement initResult)
    {
        if (initResult.ValueKind != JsonValueKind.Object
            || !initResult.TryGetProperty("authMethods", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AcpAuthMethod>();
        }

        var methods = new List<AcpAuthMethod>();
        foreach (var method in array.EnumerateArray())
        {
            if (method.ValueKind != JsonValueKind.Object)
                continue;
            var id = GetString(method, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            methods.Add(new AcpAuthMethod
            {
                Id = id,
                Name = GetString(method, "name") ?? id,
                Description = GetString(method, "description") ?? ""
            });
        }

        return methods;
    }

    /// <summary>
    /// Runs an ACP request that may fail with the auth-required code, driving
    /// the standard terminal-auth recovery on the first failure and retrying
    /// exactly once. If authentication still cannot be established, throws
    /// <see cref="AcpAuthRequiredException"/> so callers stay in a recoverable
    /// <c>auth_required</c> state instead of degrading to transcript-only.
    /// The single-attempt gate prevents an infinite login/retry loop.
    /// </summary>
    private async Task<JsonElement> SendWithAuthRetryAsync(
        Func<CancellationToken, Task<JsonElement>> send,
        CancellationToken cancellationToken,
        bool treatTimeoutAsAuthRequired = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await send(cancellationToken).ConfigureAwait(false);
            }
            catch (AcpJsonRpcException ex) when (ex.IsAuthRequired)
            {
                if (attempt == 0
                    && await RecoverFromAuthRequiredAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                throw new AcpAuthRequiredException(BuildLoginRequiredMessage());
            }
            catch (TimeoutException) when (treatTimeoutAsAuthRequired && attempt == 0)
            {
                // Some external CLIs (e.g. Qoder) hang on session/new until the
                // user finishes interactive login instead of returning an ACP
                // auth error. Map that stall onto the same recoverable path.
                if (await RecoverFromAuthRequiredAsync(cancellationToken).ConfigureAwait(false))
                    continue;

                throw new AcpAuthRequiredException(BuildLoginRequiredMessage());
            }
        }
    }

    /// <summary>
    /// Enters the recoverable <c>auth_required</c> state and attempts to
    /// re-authenticate. Returns true when credentials are now valid and the
    /// caller should retry the original request; false when the user still
    /// needs to complete an interactive login (a login terminal is opened).
    /// </summary>
    private async Task<bool> RecoverFromAuthRequiredAsync(CancellationToken cancellationToken)
    {
        _status = "auth_required";
        await PublishStateAsync().ConfigureAwait(false);

        // The user may already have valid credentials in ~/.kimi-code (e.g. a
        // prior login). authenticate() only validates the token; if it passes
        // we can resume immediately with no terminal.
        if (await TryAuthenticateAsync(cancellationToken).ConfigureAwait(false))
        {
            _status = _isRunning ? "running" : "ready";
            await PublishStateAsync().ConfigureAwait(false);
            return true;
        }

        // No valid token yet — open an interactive login terminal and leave
        // the workspace in auth_required so the user can retry after login.
        // Prefer the provider's own login profile when one exists; otherwise
        // fall back to appending --login to the ACP process spec.
        await LaunchLoginTerminalAsync().ConfigureAwait(false);
        return false;
    }

    private async Task<bool> TryAuthenticateAsync(CancellationToken cancellationToken)
    {
        var transport = _transport;
        if (transport is not { IsRunning: true })
            return false;

        var methodId = SelectAuthMethodId();
        if (methodId == null)
            return false;

        try
        {
            await transport.SendRequestAsync(
                "authenticate",
                new { methodId },
                TimeSpan.FromSeconds(30),
                GetEffectiveCancellationToken(cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (AcpJsonRpcException)
        {
            // -32000 (no token) or any other auth failure: cannot authenticate
            // silently; the caller falls back to the interactive login flow.
            return false;
        }
    }

    private string? SelectAuthMethodId()
    {
        if (_authMethods.Count == 0)
            return "login"; // ACP terminal-auth default method id.

        var login = _authMethods.FirstOrDefault(
            method => string.Equals(method.Id, "login", StringComparison.OrdinalIgnoreCase));
        return (login ?? _authMethods[0]).Id;
    }

    private async Task LaunchLoginTerminalAsync()
    {
        var profile = CreateLoginTerminalProfile();
        if (profile == null)
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "command_result",
                text = $"{_provider.Descriptor.DisplayName} 需要登录，但未提供可用的登录入口。"
            }).ConfigureAwait(false);
            return;
        }

        await _tabManagementService.CreateTabAsync(profile).ConfigureAwait(false);
        await _terminalBridgeService.SetViewModeAsync("terminal").ConfigureAwait(false);
        await _bridgeService.SendEventAsync(new
        {
            type = "command_result",
            text = BuildLoginRequiredMessage()
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a login terminal profile. Prefer the provider's declared login
    /// profile (e.g. <c>qodercli login</c>, or Qwen's interactive TUI). Otherwise
    /// append <c>--login</c> to the ACP process spec (Claude / Kimi style).
    /// </summary>
    private ShellProfile? CreateLoginTerminalProfile()
    {
        var providerProfile = _provider.CreateLoginTerminalProfile(_workingDirectory);
        if (providerProfile != null)
            return providerProfile;

        AcpProcessSpec spec;
        try
        {
            spec = _runtime.CreateProcessSpec(_workingDirectory);
        }
        catch
        {
            return null;
        }

        var args = spec.Arguments.ToList();
        if (!args.Contains("--login", StringComparer.OrdinalIgnoreCase))
            args.Add("--login");

        var command = new StringBuilder();
        command.Append("& ").Append(QuoteForPowerShell(spec.FileName));
        foreach (var arg in args)
            command.Append(' ').Append(QuoteForPowerShell(arg));

        var escapedCwd = _workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = $"{_provider.Descriptor.Key}-login",
            Name = $"{_provider.Descriptor.DisplayName} 登录",
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = _workingDirectory
        };
    }

    private static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";

    private string BuildLoginRequiredMessage()
    {
        var authHint = _authMethods
            .Select(method => method.Description)
            .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description));
        var baseMessage =
            $"{_provider.Descriptor.DisplayName} 需要登录后才能继续：已打开登录终端，请在其中完成登录（设备码或 API key），完成后返回并重新发送消息即可继续。";
        if (!string.IsNullOrWhiteSpace(authHint))
            return $"{baseMessage} {authHint}";
        return baseMessage;
    }

    private async Task StartAcpRunAsync(string prompt, IReadOnlyList<string>? attachmentIds = null)
    {
        var requestedAttachmentIds = attachmentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(prompt) && requestedAttachmentIds.Length == 0)
            return;

        if (!IsAgentRuntimeReady())
        {
            await PublishRuntimeStatusAsync().ConfigureAwait(false);
            await SendRunFailedAsync("Install the Agent runtime before sending a message.").ConfigureAwait(false);
            return;
        }

        if (_status == "restoring")
        {
            await SendRunFailedAsync("ACP history is still loading. Wait for restore to finish.").ConfigureAwait(false);
            return;
        }

        if (_status == "transcript_only")
        {
            await SendResumeFailedAsync("This conversation is available as a local transcript only. Start a new thread or open terminal to continue.").ConfigureAwait(false);
            return;
        }

        if (!Directory.Exists(_workingDirectory))
        {
            await SendRunFailedAsync($"Directory does not exist: {_workingDirectory}").ConfigureAwait(false);
            return;
        }

        if (_transportRecoveryRequired)
        {
            try
            {
                await RestoreSessionAfterTransportResetAsync().ConfigureAwait(false);
            }
            catch (AcpAuthRequiredException)
            {
                // Restore surfaced a recoverable authRequired: a login terminal
                // is already open and the workspace is in auth_required. Do not
                // degrade to transcript_only; let the user finish login and
                // re-send the prompt.
                await SendRunFailedAsync(BuildLoginRequiredMessage()).ConfigureAwait(false);
                await PublishStateAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _status = "transcript_only";
                await SendResumeFailedAsync(
                    "PSX could not restore the Agent session after reconnecting. The saved local transcript is still available to read.",
                    ex.Message).ConfigureAwait(false);
                await PublishStateAsync().ConfigureAwait(false);
                return;
            }
        }

        IReadOnlyList<AgentAttachment> attachments;
        try
        {
            attachments = ResolvePromptAttachments(requestedAttachmentIds);
        }
        catch (Exception ex)
        {
            await SendRunFailedAsync(ex.Message).ConfigureAwait(false);
            return;
        }

        string runId;
        var cts = new CancellationTokenSource();
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceLifetimeCts.Token);

        lock (_runLock)
        {
            if (_isRunning)
            {
                cts.Dispose();
                requestCts.Dispose();
                _ = SendRunFailedAsync($"{_provider.Descriptor.AssistantName} Agent is still responding. Wait for the current run to finish or use /stop.");
                return;
            }

            _isRunning = true;
            _status = "running";
            _thinkingBuffer.Clear();
            _assistantBuffer.Clear();
            lock (_sessionMutationSync)
            {
                _toolState.Clear();
            }
            _currentRunId = Guid.NewGuid().ToString();
            _runCts = cts;
            _runRequestCts = requestCts;
            runId = _currentRunId;
        }

        var runStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = Task.Run(async () =>
        {
            await runStart.Task.ConfigureAwait(false);
            var token = cts.Token;
            AcpJsonRpcTransport? usedTransport = null;
            long usedTransportGeneration = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                // Establish (and if necessary authenticate) the ACP session
                // BEFORE committing the user message to history. When login is
                // required and the user cancels it, EnsureAcpSessionAsync throws
                // and we never write a half-sent message — the composer/pending
                // prompt stays intact instead of leaving stale unsent history.
                await EnsureAcpSessionAsync(createIfMissing: true, requestCts.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                usedTransport = _transport
                    ?? throw new InvalidOperationException("ACP adapter transport was not initialized.");
                usedTransportGeneration = _transportGeneration;
                lock (_runLock)
                {
                    if (_currentRunId == runId)
                    {
                        _currentRunTransport = usedTransport;
                        _currentRunTransportGeneration = usedTransportGeneration;
                    }
                }

                // Session is ready and authenticated — now it is safe to record
                // the user message and begin the assistant turn.
                AddMessage("user", prompt, attachments: attachments);
                await _bridgeService.SendEventAsync(new
                {
                    type = "user_message",
                    text = prompt,
                    title = _currentThread.Title,
                    runId,
                    attachments = attachments.Select(ToAttachmentPayload).ToArray()
                }).ConfigureAwait(false);
                await _bridgeService.SendEventAsync(new { type = "thinking_started" }).ConfigureAwait(false);
                await PublishStateAsync().ConfigureAwait(false);

                var promptBlocks = BuildPromptBlocks(prompt, attachments);
                var capturedTransport = usedTransport;

                // No wall-clock timeout on session/prompt: a long multi-agent
                // turn streams progress via session/update and must not be killed
                // mid-flight. A real disconnect (process exit / stdout EOF / read
                // error) still faults this request through SignalDisconnected ->
                // CompletePendingWithError, so the request remains
                // user-cancellable, and real transport disconnects fault it.
                // Wrapped so a mid-conversation
                // authRequired drives the standard login recovery and retries
                // once instead of failing hard.
                await SendWithAuthRetryAsync(
                    innerToken => capturedTransport.SendRequestAsync("session/prompt", new
                    {
                        sessionId = _acpSessionId,
                        messageId = Guid.NewGuid().ToString(),
                        prompt = promptBlocks
                    }, PromptRequestTimeout, innerToken),
                    requestCts.Token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                await FinalizeRunOutputAsync(runId, cts).ConfigureAwait(false);
                await _bridgeService.SendEventAsync(new { type = "thinking_finished" }).ConfigureAwait(false);
                await _bridgeService.SendEventAsync(new { type = "assistant_message_done" }).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // Run was cancelled by user — do NOT set _status to "error".
                // CancelRunAsync already set the terminal status ("ready", or
                // "recovery_pending" when a forced reset killed the transport).
                await FinalizeRunOutputAsync(runId, cts).ConfigureAwait(false);
                if (!_disposed)
                    await _bridgeService.SendEventAsync(new { type = "thinking_finished" }).ConfigureAwait(false);
            }
            catch (AcpAuthRequiredException ex)
            {
                // Recoverable auth failure: stay in auth_required so the user
                // can finish the interactive login and resend. Never downgrade
                // to "error" or "transcript_only" here.
                await FinalizeRunOutputAsync(runId, cts).ConfigureAwait(false);
                lock (_runLock)
                {
                    if (_currentRunId == runId)
                        _status = "auth_required";
                }
                if (!_disposed)
                {
                    await _bridgeService.SendEventAsync(new { type = "thinking_finished" }).ConfigureAwait(false);
                    await SendRunFailedAsync(ex.Message, runId).ConfigureAwait(false);
                }
            }
            catch (TimeoutException ex)
            {
                await FinalizeRunOutputAsync(runId, cts).ConfigureAwait(false);
                var timedOutTransport = usedTransport ?? _transport;
                var timedOutGeneration = usedTransportGeneration != 0
                    ? usedTransportGeneration
                    : _transportGeneration;
                if (timedOutTransport != null)
                    await ResetTransportAsync(timedOutTransport, timedOutGeneration).ConfigureAwait(false);

                lock (_runLock)
                {
                    // A session/load timeout during restore can set
                    // "transcript_only" and then surface here as TimeoutException;
                    // never downgrade that terminal read-only state to "error".
                    if (_currentRunId == runId && _status != "transcript_only")
                        _status = _transportRecoveryRequired ? "recovery_pending" : "error";
                }
                if (!_disposed)
                {
                    await _bridgeService.SendEventAsync(new { type = "thinking_finished" }).ConfigureAwait(false);
                    await SendRunFailedAsync(ex.Message, runId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Real error (not cancellation). Persist any partial output first,
                // then — only if this run's transport actually died — reset it so
                // the next message recovers. A transport still alive (ordinary
                // JSON-RPC error) is left untouched: no process kill, status
                // stays "error".
                await FinalizeRunOutputAsync(runId, cts).ConfigureAwait(false);
                if (usedTransport != null && !usedTransport.IsRunning)
                    await ResetTransportAsync(usedTransport, usedTransportGeneration).ConfigureAwait(false);

                lock (_runLock)
                {
                    if (_currentRunId == runId && _status != "transcript_only")
                        _status = _transportRecoveryRequired ? "recovery_pending" : "error";
                }
                if (!_disposed)
                {
                    await _bridgeService.SendEventAsync(new { type = "thinking_finished" }).ConfigureAwait(false);
                    await SendRunFailedAsync(ex.Message, runId).ConfigureAwait(false);
                }
            }
            finally
            {
                // Only clean up state if this task is still the current run.
                // This prevents an orphaned old task from destroying a new run's state.
                lock (_runLock)
                {
                    if (_currentRunId == runId)
                    {
                        _isRunning = false;
                        // Resolve any transient live status (running, or the
                        // "stopping" set by a user cancel) to a terminal one. A
                        // forced reset that killed the transport surfaces as
                        // recovery_pending so the next prompt reloads the session.
                        if (_status == "running" || _status == "stopping")
                            _status = _transportRecoveryRequired ? "recovery_pending" : "ready";
                        _currentRunId = null;
                        _runCts = null;
                        _runRequestCts = null;
                        _currentRunTask = null;
                        _currentRunTransport = null;
                        _currentRunTransportGeneration = 0;
                    }
                }

                cts.Dispose();
                requestCts.Dispose();
                if (!_disposed)
                {
                    SaveCurrentThread();
                    await _bridgeService.SendEventAsync(new { type = "run_finished", runId }).ConfigureAwait(false);
                    await PublishStateAsync().ConfigureAwait(false);
                }
            }
        });

        lock (_runLock)
        {
            if (_currentRunId == runId)
                _currentRunTask = runTask;
        }
        runStart.TrySetResult();
    }

    private async Task EnsureAcpSessionAsync(
        bool createIfMissing,
        CancellationToken cancellationToken = default)
    {
        await EnsureTransportAsync(cancellationToken).ConfigureAwait(false);

        if (_transportRecoveryRequired)
            await RestoreSessionAfterTransportResetAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_acpSessionId))
            return;

        if (!string.IsNullOrWhiteSpace(_currentThread.AcpSessionId))
        {
            if (!createIfMissing)
                return;

            throw new InvalidOperationException("ACP session context is not restored. Reload the thread from History or start a new thread.");
        }

        if (!createIfMissing)
            return;

        var result = await SendWithAuthRetryAsync(
            token => _transport!.SendRequestAsync(
                "session/new",
                _provider.CreateNewSessionParameters(_workingDirectory),
                _provider.NewSessionTimeout,
                GetEffectiveCancellationToken(token)),
            cancellationToken,
            treatTimeoutAsAuthRequired: _provider.TreatNewSessionTimeoutAsAuthRequired).ConfigureAwait(false);

        _acpSessionId = GetString(result, "sessionId");
        _sessionIdForTransportRecovery = null;
        _transportRecoveryRequired = false;
        lock (_sessionMutationSync)
        {
            _currentThread.Provider = _provider.Descriptor.Key;
            _currentThread.AcpSessionId = _acpSessionId;
            _currentThread.AdapterVersion = _adapterVersion;
            CaptureModes(result);
            CaptureConfigOptions(result);
            SaveCurrentThreadCore();
        }
        await SendSessionReadyAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// How incoming <c>session/update</c> notifications are treated while a
    /// restore request (<c>session/load</c> or <c>session/resume</c>) is in
    /// flight.
    /// </summary>
    private enum SessionRestoreMode
    {
        /// <summary>No restore in flight: updates flow through the live pipeline.</summary>
        None,

        /// <summary>No local transcript: replay chunks are aggregated to rebuild it.</summary>
        ReplayTranscript,

        /// <summary>
        /// The local transcript is authoritative: only control updates
        /// (commands, usage, config, mode, title) are applied; content replay
        /// is dropped on arrival.
        /// </summary>
        ControlOnly
    }

    private async Task<bool> LoadAcpHistoryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTransportAsync(cancellationToken).ConfigureAwait(false);

        // The locally persisted transcript is authoritative: the live pipeline
        // saved it in its aggregated per-turn shape (user → thinking → tools →
        // assistant). When it exists, the restore request only re-establishes
        // the agent-side session context — replayed content must never
        // overwrite the archive, because providers may replay injected context
        // as user chunks and fragment assistant text around tool events.
        bool hasLocalTranscript;
        AgentMessage[] documentDecisionSnapshots;
        lock (_sessionMutationSync)
        {
            hasLocalTranscript = _currentThread.Messages.Count > 0;
            documentDecisionSnapshots = _currentThread.Messages
                .Where(DocumentDecisionSnapshotMerger.IsDocumentDecision)
                .Select(DocumentDecisionSnapshotMerger.CloneMessage)
                .ToArray();
        }
        var replay = new ReplayHistoryState();
        // With a local transcript only control updates are applied and content
        // replay is dropped on arrival: large sessions otherwise pay full CPU
        // and memory for a replay state that is discarded anyway.
        _restoreMode = hasLocalTranscript
            ? SessionRestoreMode.ControlOnly
            : SessionRestoreMode.ReplayTranscript;
        _replayHistory = hasLocalTranscript ? null : replay;
        _status = "restoring";
        await PublishStateAsync().ConfigureAwait(false);

        try
        {
            var restoreResult = await SendRestoreRequestAsync(sessionId, hasLocalTranscript, cancellationToken)
                .ConfigureAwait(false);

            _acpSessionId = sessionId;
            _sessionIdForTransportRecovery = null;
            _transportRecoveryRequired = false;
            // session/resume succeeds with an empty result object; it must not
            // clear the modes a session/load result would have carried.
            if (restoreResult.ValueKind == JsonValueKind.Object && restoreResult.TryGetProperty("modes", out _))
                CaptureModes(restoreResult);
            CaptureConfigOptions(restoreResult);

            if (hasLocalTranscript)
            {
                // Keep the archive. A still-pending mode transition cannot be
                // answered after a reload, so mark it interrupted locally.
                bool interrupted;
                lock (_sessionMutationSync)
                {
                    interrupted = DocumentDecisionSnapshotMerger.InterruptPending(_currentThread);
                    // One unified persist for everything ControlOnly captured in
                    // memory (usage, mode, title) plus the interruption above,
                    // instead of a disk write per control update.
                    SaveCurrentThreadCore();
                }
                if (interrupted)
                    await SendThreadLoadedAsync(clear: true).ConfigureAwait(false);

                await SendSessionReadyAsync().ConfigureAwait(false);
                return true;
            }

            // No local archive (e.g. a thread imported by session id only):
            // rebuild the transcript from the replayed chunk stream.
            FinishReplayHistory(replay);
            DocumentDecisionSnapshotMerger.Merge(replay.Messages, documentDecisionSnapshots);
            ThinkingMessageNormalizer.Normalize(replay.Messages);

            if (replay.Messages.Count > 0)
            {
                lock (_sessionMutationSync)
                {
                    if (replay.Messages[0].Role == "user")
                        _currentThread.Title = BuildMessageTitle(replay.Messages[0].Text);
                    _currentThread.Messages = replay.Messages;
                    SaveCurrentThreadCore();
                }
                await SendThreadLoadedAsync(clear: true).ConfigureAwait(false);
            }
            else
            {
                _acpSessionId = null;
                await _bridgeService.SendEventAsync(new { type = "command_result", text = "ACP session loaded, but no transcript was replayed. Keeping the local snapshot." }).ConfigureAwait(false);
            }

            await SendSessionReadyAsync().ConfigureAwait(false);
            return replay.Messages.Count > 0;
        }
        finally
        {
            _restoreMode = SessionRestoreMode.None;
            _replayHistory = null;
        }
    }

    /// <summary>
    /// Sends the restore request for an existing agent-side session. With a
    /// local transcript and a declared resume capability this uses
    /// <c>session/resume</c>, which re-establishes the session without any
    /// history replay; everything else uses <c>session/load</c>. Only a
    /// <c>-32601 Method not found</c> from an agent that advertised resume
    /// without implementing it falls back to load — auth failures, timeouts
    /// and missing sessions propagate unchanged into the existing restore
    /// error handling.
    /// </summary>
    private async Task<JsonElement> SendRestoreRequestAsync(
        string sessionId,
        bool hasLocalTranscript,
        CancellationToken cancellationToken)
    {
        if (hasLocalTranscript && _supportsSessionResume)
        {
            try
            {
                return await SendWithAuthRetryAsync(
                    token => _transport!.SendRequestAsync(
                        "session/resume",
                        _provider.CreateRestoreSessionParameters(sessionId, _workingDirectory),
                        TimeSpan.FromSeconds(45),
                        GetEffectiveCancellationToken(token)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcpJsonRpcException exception) when (exception.Code == AcpJsonRpcException.MethodNotFoundCode)
            {
                // Advertised but unimplemented: defensive one-shot fallback.
            }
        }

        return await SendWithAuthRetryAsync(
            token => _transport!.SendRequestAsync(
                "session/load",
                _provider.CreateRestoreSessionParameters(sessionId, _workingDirectory),
                TimeSpan.FromSeconds(45),
                GetEffectiveCancellationToken(token)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureTransportAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsBoundProviderThread(_currentThread))
            throw new InvalidOperationException(BuildUnsupportedProviderMessage(_currentThread.Provider));

        var effectiveToken = GetEffectiveCancellationToken(cancellationToken);
        await _transportLock.WaitAsync(effectiveToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transport?.IsRunning == true)
                return;

            if (_transport != null)
                ResetTransportCore(_transport, _transportGeneration);

            if (!IsAgentRuntimeReady())
            {
                throw new InvalidOperationException(
                    "Agent runtime is not installed. Open Agent mode and choose Install runtime first.");
            }

            var logDirectory = Path.Combine(_threadStore.RootDirectory, "agent", "acp-logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(
                logDirectory,
                $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{WorkspaceId:N}.ndjson.log");
            var generation = ++_transportGeneration;
            var transportLifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceLifetimeCts.Token);
            var transport = new AcpJsonRpcTransport(
                _runtime.CreateProcessSpec(_workingDirectory),
                logPath,
                HandleAgentRequestAsync,
                notification => HandleAgentNotificationAsync(notification, generation));

            _transport = transport;
            _transportLifetimeCts = transportLifetimeCts;

            try
            {
                transport.Start();
                using var initializeCts = CancellationTokenSource.CreateLinkedTokenSource(
                    effectiveToken,
                    transportLifetimeCts.Token);
                var initResult = await transport.SendRequestAsync("initialize", new
                {
                    protocolVersion = 1,
                    clientCapabilities = BuildClientCapabilities(_provider.ClientCapabilities),
                    clientInfo = new
                    {
                        name = "PSX",
                        version = "0.1.0"
                    }
                }, TimeSpan.FromSeconds(30), initializeCts.Token).ConfigureAwait(false);

                _adapterVersion = ReadNestedString(initResult, "agentInfo", "version");
                _supportsImage = ReadNestedBool(initResult, "agentCapabilities", "promptCapabilities", "image") ?? false;
                _supportsSessionResume = SupportsSessionResume(initResult);
                _authMethods = ParseAuthMethods(initResult);
                lock (_sessionMutationSync)
                {
                    _currentThread.AdapterVersion = _adapterVersion;
                    SaveCurrentThreadCore();
                }
                await PublishStateAsync().ConfigureAwait(false);
            }
            catch
            {
                ResetTransportCore(transport, generation);
                throw;
            }
        }
        finally
        {
            _transportLock.Release();
        }
    }

    internal static Dictionary<string, object?> BuildClientCapabilities(AcpClientCapabilityProfile profile)
    {
        var capabilities = new Dictionary<string, object?>();

        // Omit the fs key entirely when a provider opts out of the reverse
        // filesystem bridge so the agent falls back to its own local file tools.
        if (profile.FileSystemReadText || profile.FileSystemWriteText)
        {
            capabilities["fs"] = new
            {
                readTextFile = profile.FileSystemReadText,
                writeTextFile = profile.FileSystemWriteText
            };
        }

        if (profile.Terminal)
            capabilities["terminal"] = true;

        if (profile.SessionBooleanConfig)
            capabilities["session"] = new { configOptions = new { boolean = new { } } };

        if (profile.ElicitationFormUrl)
            capabilities["elicitation"] = new { form = new { }, url = new { } };

        if (profile.TerminalOutputMeta)
            capabilities["_meta"] = new Dictionary<string, object?> { ["terminal_output"] = true };

        return capabilities;
    }

    private async Task RestoreSessionAfterTransportResetAsync(CancellationToken cancellationToken = default)
    {
        var effectiveToken = GetEffectiveCancellationToken(cancellationToken);
        await _sessionRestoreLock.WaitAsync(effectiveToken).ConfigureAwait(false);
        try
        {
            if (!_transportRecoveryRequired)
                return;

            var sessionId = _sessionIdForTransportRecovery ?? _currentThread.AcpSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _transportRecoveryRequired = false;
                return;
            }

            var restored = await LoadAcpHistoryAsync(sessionId, effectiveToken).ConfigureAwait(false);
            if (!restored)
            {
                _transportRecoveryRequired = false;
                _status = "transcript_only";
                throw new InvalidOperationException("ACP session reconnected, but no transcript was replayed.");
            }

            _sessionIdForTransportRecovery = null;
            _transportRecoveryRequired = false;
            _status = _isRunning ? "running" : "restored";
        }
        catch (AcpAuthRequiredException)
        {
            // session/load hit authRequired: this is recoverable. Keep the
            // transport (it is still valid) and stay in auth_required so the
            // user can finish the interactive login and retry — never degrade
            // to a permanent transcript_only fallback.
            _status = "auth_required";
            await PublishStateAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var failedTransport = _transport;
            var failedGeneration = _transportGeneration;
            if (failedTransport != null && (ex is TimeoutException || !failedTransport.IsRunning))
                await ResetTransportAsync(failedTransport, failedGeneration).ConfigureAwait(false);

            _transportRecoveryRequired = false;
            _status = "transcript_only";
            throw;
        }
        finally
        {
            _sessionRestoreLock.Release();
        }
    }

    private async Task ResetTransportAsync(AcpJsonRpcTransport expectedTransport, long expectedGeneration)
    {
        if (await _transportLock.WaitAsync(TransportResetLockBudget).ConfigureAwait(false))
        {
            try
            {
                ResetTransportCore(expectedTransport, expectedGeneration);
            }
            finally
            {
                _transportLock.Release();
            }
        }
        else
        {
            // A stalled operation holds the transport lock. Never wait forever:
            // dispose the transport directly (idempotent, non-blocking, kills the
            // whole process tree) so the forced reset always completes. Remaining
            // state cleanup runs when the lock frees.
            expectedTransport.Dispose();
        }
    }

    private bool ResetTransportCore(AcpJsonRpcTransport expectedTransport, long expectedGeneration)
    {
        if (!ReferenceEquals(_transport, expectedTransport) || _transportGeneration != expectedGeneration)
            return false;

        ClearAvailableAgentCommands();
        if (!_disposed)
            _ = SendAgentCommandsUnavailableAsync();
        _sessionIdForTransportRecovery ??= _acpSessionId ?? _currentThread.AcpSessionId;
        _acpSessionId = null;
        _transportRecoveryRequired = !string.IsNullOrWhiteSpace(_sessionIdForTransportRecovery);

        try { _transportLifetimeCts?.Cancel(); } catch { }
        _transportLifetimeCts?.Dispose();
        _transportLifetimeCts = null;

        _transport = null;
        expectedTransport.Dispose();
        CleanupTerminalsForGeneration(expectedGeneration);
        return true;
    }

    private void CleanupTerminalsForGeneration(long generation)
    {
        foreach (var item in _terminals.ToArray())
        {
            if (item.Value.TransportGeneration != generation)
                continue;

            if (_terminals.TryRemove(item.Key, out var terminal))
                StopAndDisposeTerminal(terminal);
        }
    }

    private static void StopAndDisposeTerminal(AcpTerminalProcess terminal)
    {
        try
        {
            if (!terminal.Process.HasExited)
                terminal.Process.Kill(entireProcessTree: true);
        }
        catch { }

        terminal.Process.Dispose();
    }

    private CancellationToken GetEffectiveCancellationToken(CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? cancellationToken
            : _serviceLifetimeCts.Token;
    }

    private async Task<object?> HandleAgentRequestAsync(JsonElement request)
    {
        var method = GetString(request, "method");
        var parameters = request.TryGetProperty("params", out var p) ? p : default;

        return method switch
        {
            "session/request_permission" => await HandlePermissionRequestAsync(request, parameters).ConfigureAwait(false),
            "elicitation/create" => await HandleElicitationCreateAsync(request, parameters).ConfigureAwait(false),
            "fs/read_text_file" => HandleReadTextFile(parameters),
            "fs/write_text_file" => HandleWriteTextFile(parameters),
            "terminal/create" => HandleCreateTerminal(parameters),
            "terminal/output" => HandleTerminalOutput(parameters),
            "terminal/wait_for_exit" => await HandleWaitForTerminalExitAsync(parameters).ConfigureAwait(false),
            "terminal/kill" => HandleKillTerminal(parameters),
            "terminal/release" => HandleReleaseTerminal(parameters),
            _ => new { }
        };
    }

    private async Task<object?> HandlePermissionRequestAsync(JsonElement request, JsonElement parameters)
    {
        var requestId = request.GetProperty("id").ToString();
        var options = AcpPermissionPolicy.ReadOptions(parameters);

        var toolCall = parameters.TryGetProperty("toolCall", out var tc) ? tc : default;
        var toolCallId = GetString(toolCall, "toolCallId");
        var toolKind = GetString(toolCall, "kind");
        var toolStatus = GetString(toolCall, "status");
        var classified = AcpPermissionPolicy.Classify(toolCall);
        var presentation = classified.Presentation switch
        {
            AcpPermissionPresentation.ModeTransition => PermissionPresentation.ModeTransition,
            AcpPermissionPresentation.Document => PermissionPresentation.Document,
            _ => PermissionPresentation.Ordinary
        };
        var documentText = classified.DocumentText;
        var description = classified.Description;
        var explicitToolInput = classified.ExplicitRawInput;

        // Structural ask-user lift: nested rawInput.questions → permission form
        // variant (same pending table / agent_permission_response channel).
        // Never emit elicitation_request for this — that would break pairing.
        var isAskUserForm = false;
        object? askUserSchema = null;
        string? askUserMessage = null;
        IReadOnlyList<int> askUserIndexes = Array.Empty<int>();
        string? formSubmitOptionId = null;
        if (presentation == PermissionPresentation.Ordinary
            && AcpAskUserQuestionAdapter.TryCreateForm(
                toolCall,
                out askUserMessage,
                out askUserSchema,
                out askUserIndexes))
        {
            formSubmitOptionId = AcpAskUserQuestionAdapter.ResolveProceedOptionId(options);
            if (!string.IsNullOrWhiteSpace(formSubmitOptionId))
            {
                isAskUserForm = true;
                explicitToolInput = null;
                description = "";
            }
        }

        var pending = new PendingPermission
        {
            Options = options,
            Presentation = presentation,
            DecisionSnapshotId = Guid.NewGuid().ToString("N"),
            IsAskUserForm = isAskUserForm,
            AskUserAnswerIndexes = askUserIndexes,
            FormSubmitOptionId = formSubmitOptionId
        };
        _pendingPermissions[requestId] = pending;

        var title = GetString(toolCall, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = GetString(toolCall, "name") ?? $"{_provider.Descriptor.AssistantName} permission request";
        }

        if (pending.IsDocumentDecision)
        {
            lock (_sessionMutationSync)
            {
                if (!string.IsNullOrWhiteSpace(toolCallId))
                    _toolState.MarkDocumentDecision(toolCallId);
            }

            UpsertDocumentDecisionMessage(
                pending.Presentation,
                pending.DecisionSnapshotId,
                requestId,
                toolCallId,
                title,
                documentText,
                options,
                "pending");
        }

        await _bridgeService.SendEventAsync(new
        {
            type = "permission_request",
            requestId,
            decisionSnapshotId = pending.DecisionSnapshotId,
            title,
            text = explicitToolInput,
            description = pending.IsDocumentDecision || string.IsNullOrWhiteSpace(description)
                ? null
                : description,
            presentation = pending.IsAskUserForm
                ? "form"
                : pending.Presentation switch
                {
                    PermissionPresentation.ModeTransition => "mode_transition",
                    PermissionPresentation.Document => "document",
                    _ => null
                },
            message = pending.IsAskUserForm ? askUserMessage : null,
            schema = pending.IsAskUserForm ? askUserSchema : null,
            formSubmitOptionId = pending.IsAskUserForm ? formSubmitOptionId : null,
            toolCallId,
            toolKind,
            toolStatus,
            documentText = pending.IsDocumentDecision ? documentText : null,
            options = options.Select(option => new
            {
                optionId = option.OptionId,
                name = option.Name,
                kind = option.Kind
            }).ToArray()
        }).ConfigureAwait(false);

        if (options.Length == 0)
        {
            _pendingPermissions.TryRemove(requestId, out _);
            if (pending.IsDocumentDecision)
                UpdateDocumentDecisionState(requestId, pending.Presentation, "cancelled", decisionSnapshotId: pending.DecisionSnapshotId);
            await _bridgeService.SendEventAsync(new
            {
                type = "permission_cancelled",
                requestId,
                text = "The ACP Agent did not provide any response options."
            }).ConfigureAwait(false);
            return new { outcome = new { outcome = "cancelled" } };
        }

        var selectedRaw = await pending.Completion.Task.ConfigureAwait(false);
        if (selectedRaw == "__cancelled__")
        {
            if (pending.IsDocumentDecision)
                UpdateDocumentDecisionState(requestId, pending.Presentation, "cancelled", decisionSnapshotId: pending.DecisionSnapshotId);
            return new { outcome = new { outcome = "cancelled" } };
        }

        if (!AcpAskUserQuestionAdapter.TryParsePermissionResponseValue(
                selectedRaw,
                out var selected,
                out var formContent,
                out var hasFormContent))
        {
            if (pending.IsDocumentDecision)
                UpdateDocumentDecisionState(requestId, pending.Presentation, "cancelled", decisionSnapshotId: pending.DecisionSnapshotId);

            await _bridgeService.SendEventAsync(new
            {
                type = "permission_cancelled",
                requestId,
                text = "The selected option was not offered by the ACP Agent."
            }).ConfigureAwait(false);
            return new { outcome = new { outcome = "cancelled" } };
        }

        var selectedOption = AcpPermissionPolicy.FindOfferedOption(pending.Options, selected);
        if (selectedOption == null)
        {
            if (pending.IsDocumentDecision)
                UpdateDocumentDecisionState(requestId, pending.Presentation, "cancelled", decisionSnapshotId: pending.DecisionSnapshotId);

            await _bridgeService.SendEventAsync(new
            {
                type = "permission_cancelled",
                requestId,
                text = "The selected option was not offered by the ACP Agent."
            }).ConfigureAwait(false);
            return new { outcome = new { outcome = "cancelled" } };
        }

        selected = selectedOption.OptionId;
        if (pending.IsDocumentDecision)
            UpdateDocumentDecisionState(requestId, pending.Presentation, "selected", selected, pending.DecisionSnapshotId);

        await _bridgeService.SendEventAsync(new
        {
            type = "permission_resolved",
            requestId,
            optionId = selected,
            optionName = selectedOption.Name
        }).ConfigureAwait(false);

        Dictionary<string, string>? answers = null;
        if (pending.IsAskUserForm
            && hasFormContent
            && string.Equals(selected, pending.FormSubmitOptionId, StringComparison.OrdinalIgnoreCase))
        {
            answers = AcpAskUserQuestionAdapter.MapContentToAnswers(
                formContent,
                pending.AskUserAnswerIndexes);
        }

        if (answers is { Count: > 0 })
        {
            return new Dictionary<string, object?>
            {
                ["outcome"] = new Dictionary<string, object?>
                {
                    ["outcome"] = "selected",
                    ["optionId"] = selected
                },
                ["answers"] = answers
            };
        }

        return new
        {
            outcome = new
            {
                outcome = "selected",
                optionId = selected
            }
        };
    }

    private async Task<object?> HandleElicitationCreateAsync(JsonElement request, JsonElement parameters)
    {
        var requestId = request.GetProperty("id").ToString();
        var pending = new PendingElicitation();
        _pendingElicitations[requestId] = pending;

        await _bridgeService.SendEventAsync(new
        {
            type = "elicitation_request",
            requestId,
            mode = GetString(parameters, "mode"),
            message = GetString(parameters, "message", $"{_provider.Descriptor.AssistantName} needs more information."),
            schema = parameters.TryGetProperty("requestedSchema", out var schema) ? JsonElementToObject(schema) : null,
            url = GetString(parameters, "url")
        }).ConfigureAwait(false);

        var responseJson = await pending.Completion.Task.ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var action = GetString(root, "action", "cancel");
            if (action is "decline" or "cancel")
                return new { action };

            if (root.TryGetProperty("content", out var content))
            {
                return new
                {
                    action = "accept",
                    content = JsonElementToObject(content)
                };
            }

            return new { action = "accept", content = new { } };
        }
        catch
        {
            return new { action = "cancel" };
        }
    }

    private object HandleReadTextFile(JsonElement parameters)
    {
        const int MaxByteLength = 200_000;
        var path = EnsureAllowedPath(GetString(parameters, "path"));
        var line = TryGetInt(parameters, "line");
        var limit = TryGetInt(parameters, "limit");
        var start = Math.Max((line ?? 1) - 1, 0);

        using var reader = new StreamReader(path, Encoding.UTF8);
        for (var skipped = 0; skipped < start && !reader.EndOfStream; skipped++)
            reader.ReadLine();

        var lines = new List<string>();
        var byteCount = 0;
        var lineCount = 0;
        var newlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);

        while (!reader.EndOfStream)
        {
            if (limit is > 0 && lineCount >= limit.Value)
                break;

            var nextLine = reader.ReadLine();
            if (nextLine == null)
                break;

            var lineBytes = Encoding.UTF8.GetByteCount(nextLine);
            var addedBytes = lines.Count > 0 ? newlineBytes + lineBytes : lineBytes;
            if (byteCount + addedBytes > MaxByteLength)
                break;

            lines.Add(nextLine);
            byteCount += addedBytes;
            lineCount++;
        }

        return new { content = string.Join(Environment.NewLine, lines) };
    }

    private object HandleWriteTextFile(JsonElement parameters)
    {
        var path = EnsureAllowedPath(GetString(parameters, "path"));
        var content = GetString(parameters, "content");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        return new { };
    }

    private object HandleCreateTerminal(JsonElement parameters)
    {
        var command = GetString(parameters, "command");
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("ACP terminal/create did not include a command.");

        var args = parameters.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
            ? argsElement.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
            : Array.Empty<string>();
        var cwd = GetString(parameters, "cwd");
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = _workingDirectory;
        cwd = EnsureAllowedDirectory(cwd);

        var terminalId = Guid.NewGuid().ToString();
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (parameters.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in envElement.EnumerateArray())
            {
                var name = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    startInfo.Environment[name] = GetString(item, "value");
            }
        }

        const int MaxOutputByteLimit = 2_000_000;
        var rawLimit = TryGetInt(parameters, "outputByteLimit") ?? 200_000;
        var limit = Math.Clamp(rawLimit, 0, MaxOutputByteLimit);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start ACP terminal command: {command}");

        var terminal = new AcpTerminalProcess
        {
            Process = process,
            OutputByteLimit = limit,
            TransportGeneration = _transportGeneration,
            TransportToken = _transportLifetimeCts?.Token ?? _serviceLifetimeCts.Token
        };
        _terminals[terminalId] = terminal;
        _ = Task.Run(() => ReadTerminalStreamAsync(terminalId, process.StandardOutput));
        _ = Task.Run(() => ReadTerminalStreamAsync(terminalId, process.StandardError));

        return new { terminalId };
    }

    private object HandleTerminalOutput(JsonElement parameters)
    {
        var terminal = GetTerminal(GetString(parameters, "terminalId"));
        lock (terminal.Output)
        {
            return new
            {
                output = terminal.Output.ToString(),
                truncated = terminal.Truncated,
                exitStatus = terminal.Process.HasExited
                    ? new { exitCode = terminal.Process.ExitCode, signal = (string?)null }
                    : null
            };
        }
    }

    private async Task<object> HandleWaitForTerminalExitAsync(JsonElement parameters)
    {
        var terminal = GetTerminal(GetString(parameters, "terminalId"));
        await terminal.Process.WaitForExitAsync(terminal.TransportToken).ConfigureAwait(false);
        return new
        {
            exitCode = terminal.Process.ExitCode,
            signal = (string?)null
        };
    }

    private object HandleKillTerminal(JsonElement parameters)
    {
        var terminal = GetTerminal(GetString(parameters, "terminalId"));
        try
        {
            if (!terminal.Process.HasExited)
                terminal.Process.Kill(entireProcessTree: true);
        }
        catch { }

        return new { };
    }

    private object HandleReleaseTerminal(JsonElement parameters)
    {
        var terminalId = GetString(parameters, "terminalId");
        if (_terminals.TryRemove(terminalId, out var terminal))
            terminal.Process.Dispose();
        return new { };
    }

    private async Task ReadTerminalStreamAsync(string terminalId, StreamReader reader)
    {
        try
        {
            var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count <= 0)
                    break;

                if (!_terminals.TryGetValue(terminalId, out var terminal))
                    break;

                lock (terminal.Output)
                {
                    terminal.OutputByteCount = AppendBoundedUtf8(
                        terminal.Output,
                        terminal.OutputByteCount,
                        buffer.AsSpan(0, count),
                        terminal.OutputByteLimit,
                        out var truncated);
                    terminal.Truncated |= truncated;
                }
            }
        }
        catch (Exception) when (!_terminals.ContainsKey(terminalId) || _disposed)
        {
            // The transport reset or service shutdown disposed the stream.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ReadTerminalStreamAsync failed for terminal {terminalId}: {ex}");
        }
    }

    /// <summary>
    /// Appends UTF-16 text while retaining at most <paramref name="byteLimit"/>
    /// UTF-8 bytes. The oldest complete Unicode scalars are removed in one
    /// StringBuilder operation, avoiding one allocation/removal per character.
    /// </summary>
    internal static int AppendBoundedUtf8(
        StringBuilder output,
        int currentByteCount,
        ReadOnlySpan<char> text,
        int byteLimit,
        out bool truncated)
    {
        var joinsSplitSurrogate = output.Length > 0
            && text.Length > 0
            && char.IsHighSurrogate(output[^1])
            && char.IsLowSurrogate(text[0]);
        var appendedBytes = Encoding.UTF8.GetByteCount(text);
        // Each half was previously counted as a three-byte replacement. Once
        // joined they form one four-byte scalar, so correct the rolling count.
        if (joinsSplitSurrogate)
            appendedBytes -= 2;

        output.Append(text);
        var byteCount = currentByteCount + appendedBytes;
        var excess = byteCount - Math.Max(0, byteLimit);
        if (excess <= 0)
        {
            truncated = false;
            return byteCount;
        }

        var removeChars = 0;
        var removedBytes = 0;
        while (removedBytes < excess && removeChars < output.Length)
        {
            var current = output[removeChars];
            if (char.IsHighSurrogate(current)
                && removeChars + 1 < output.Length
                && char.IsLowSurrogate(output[removeChars + 1]))
            {
                removeChars += 2;
                removedBytes += 4;
            }
            else
            {
                removeChars++;
                removedBytes += current <= 0x7f ? 1 : current <= 0x7ff ? 2 : 3;
            }
        }

        if (removeChars > 0)
            output.Remove(0, removeChars);
        truncated = removeChars > 0;
        return byteCount - removedBytes;
    }

    private AcpTerminalProcess GetTerminal(string terminalId)
    {
        if (!_terminals.TryGetValue(terminalId, out var terminal))
            throw new InvalidOperationException($"ACP terminal not found: {terminalId}");
        return terminal;
    }

    private Task HandleAgentNotificationAsync(JsonElement notification, long generation)
    {
        if (_transport == null || _transportGeneration != generation)
            return Task.CompletedTask;

        var method = GetString(notification, "method");
        if (method != "session/update")
            return Task.CompletedTask;

        if (!notification.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("update", out var update))
        {
            return Task.CompletedTask;
        }

        return HandleSessionUpdateAsync(update);
    }

    private async Task HandleSessionUpdateAsync(JsonElement update)
    {
        switch (_restoreMode)
        {
            // During a restore request the update stream is replay, never
            // live: it is either aggregated to rebuild a missing transcript or
            // reduced to control updates when the local archive is
            // authoritative.
            case SessionRestoreMode.ReplayTranscript:
                await HandleReplaySessionUpdateAsync(update, _replayHistory!).ConfigureAwait(false);
                return;
            case SessionRestoreMode.ControlOnly:
                await HandleControlOnlySessionUpdateAsync(update).ConfigureAwait(false);
                return;
        }

        switch (AcpSessionUpdateReader.ReadKind(update))
        {
            case AcpSessionUpdateKind.UserMessageChunk:
                break;
            case AcpSessionUpdateKind.AgentMessageChunk:
                await SendAssistantTextAsync(AcpSessionUpdateReader.ReadContentText(update)).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.AgentThoughtChunk:
                await SendThinkingTextAsync(AcpSessionUpdateReader.ReadContentText(update)).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.ToolCall:
                await HandleToolCallAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.ToolCallUpdate:
                await HandleToolCallUpdateAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.Plan:
                await HandlePlanUpdateAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.AvailableCommands:
                await SendAvailableCommandsAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.Usage:
                await HandleUsageUpdateAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.ConfigOption:
                CaptureConfigOptions(update);
                await SendConfigOptionsAsync().ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.CurrentMode:
                await ApplyCurrentModeUpdateAsync(update, persist: true).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.SessionInfo:
                ApplySessionInfoUpdate(update, persist: true);
                break;
        }
    }

    private async Task HandleReplaySessionUpdateAsync(JsonElement update, ReplayHistoryState replay)
    {
        switch (AcpSessionUpdateReader.ReadKind(update))
        {
            case AcpSessionUpdateKind.UserMessageChunk:
                AppendReplayUserMessage(replay, AcpSessionUpdateReader.ReadContentText(update));
                break;
            case AcpSessionUpdateKind.AgentThoughtChunk:
                AppendReplayThinking(replay, AcpSessionUpdateReader.ReadContentText(update));
                break;
            case AcpSessionUpdateKind.AgentMessageChunk:
                AppendReplayAssistantMessage(replay, AcpSessionUpdateReader.ReadContentText(update));
                break;
            case AcpSessionUpdateKind.ToolCall:
                CaptureReplayToolCall(replay, update);
                break;
            case AcpSessionUpdateKind.ToolCallUpdate:
                CaptureReplayToolUpdate(replay, update);
                break;
            case AcpSessionUpdateKind.Plan:
                UpsertReplayPlan(replay, update);
                break;
            case AcpSessionUpdateKind.AvailableCommands:
                await SendAvailableCommandsAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.Usage:
                await HandleUsageUpdateAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.ConfigOption:
                break;
            case AcpSessionUpdateKind.CurrentMode:
                await ApplyCurrentModeUpdateAsync(update, persist: false).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.SessionInfo:
                ApplySessionInfoUpdate(update, persist: false);
                break;
        }
    }

    /// <summary>
    /// Applies a control update while the local transcript is authoritative:
    /// commands, usage, config, mode and title still flow to the frontend and
    /// the in-memory thread, but content replay is dropped and nothing is
    /// persisted per update — the restore path saves the thread once when the
    /// restore request completes.
    /// </summary>
    private async Task HandleControlOnlySessionUpdateAsync(JsonElement update)
    {
        switch (AcpSessionUpdateReader.ReadKind(update))
        {
            case AcpSessionUpdateKind.AvailableCommands:
                await SendAvailableCommandsAsync(update).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.Usage:
                await HandleUsageUpdateAsync(update, persist: false).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.ConfigOption:
                CaptureConfigOptions(update);
                await SendConfigOptionsAsync().ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.CurrentMode:
                await ApplyCurrentModeUpdateAsync(update, persist: false).ConfigureAwait(false);
                break;
            case AcpSessionUpdateKind.SessionInfo:
                ApplySessionInfoUpdate(update, persist: false);
                break;
        }
    }

    private Task ApplyCurrentModeUpdateAsync(JsonElement update, bool persist)
    {
        lock (_sessionMutationSync)
        {
            _currentModeId = GetString(update, "currentModeId");
            _currentThread.ModeId = _currentModeId;
            if (persist)
                SaveCurrentThreadCore();
        }
        return SendModesAsync();
    }

    private void ApplySessionInfoUpdate(JsonElement update, bool persist)
    {
        if (!update.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
            return;

        lock (_sessionMutationSync)
        {
            _currentThread.Title = title.GetString() ?? _currentThread.Title;
            if (persist)
                SaveCurrentThreadCore();
        }
    }

    private async Task HandleToolCallAsync(JsonElement update)
    {
        await ApplyLiveToolUpdateAsync(update).ConfigureAwait(false);
    }

    private async Task HandleToolCallUpdateAsync(JsonElement update)
    {
        await ApplyLiveToolUpdateAsync(update).ConfigureAwait(false);
    }

    /// <summary>
    /// ACP tool_call / tool_call_update merger: present fields replace; only
    /// <c>_meta.terminal_output.data</c> appends via <c>tool_delta</c>.
    /// </summary>
    private async Task ApplyLiveToolUpdateAsync(JsonElement update)
    {
        AcpToolUpdateResult result;
        lock (_sessionMutationSync)
            result = _toolState.ApplyUpdate(update, ReadToolName);

        if (result.Suppressed || result.Snapshot == null)
            return;

        var snapshot = result.Snapshot;
        if (result.Started && result.StartSnapshot != null)
        {
            var started = result.StartSnapshot;
            await _bridgeService.SendEventAsync(new
            {
                type = "tool_started",
                name = started.Name,
                input = started.Input,
                runId = _currentRunId,
                toolCallId = started.ToolCallId,
                summary = started.Summary,
                status = "running"
            }).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(result.TerminalDelta))
        {
            await _bridgeService.SendEventAsync(new
            {
                type = "tool_delta",
                text = result.TerminalDelta,
                runId = _currentRunId,
                toolCallId = snapshot.ToolCallId
            }).ConfigureAwait(false);
        }

        if (result.SnapshotChanged)
            await SendToolUpdatedSnapshotAsync(snapshot).ConfigureAwait(false);

        if (result.IsTerminal)
            await FinishToolAsync(snapshot.ToolCallId, snapshot.Status).ConfigureAwait(false);
    }

    private Task SendToolUpdatedSnapshotAsync(AcpToolSnapshot snapshot)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "tool_updated",
            runId = _currentRunId,
            toolCallId = snapshot.ToolCallId,
            name = snapshot.Name,
            summary = snapshot.Summary,
            input = snapshot.Input,
            output = snapshot.Output,
            status = snapshot.Status
        });
    }

    private static void AppendReplayUserMessage(ReplayHistoryState replay, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        FlushReplayTurn(replay);
        if (replay.Messages.LastOrDefault()?.Role == "user")
        {
            replay.Messages[^1].Text += text;
            return;
        }

        replay.TurnIndex++;
        replay.CurrentRunId = $"history-{replay.TurnIndex}";
        replay.Messages.Add(new AgentMessage
        {
            Role = "user",
            Text = text,
            RunId = replay.CurrentRunId,
            CreatedAt = DateTimeOffset.Now
        });
    }

    private static void AppendReplayThinking(ReplayHistoryState replay, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (string.IsNullOrWhiteSpace(replay.CurrentRunId))
            replay.CurrentRunId = $"history-{++replay.TurnIndex}";

        replay.ThinkingBuffer.Append(text);
    }

    private static void AppendReplayAssistantMessage(ReplayHistoryState replay, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (string.IsNullOrWhiteSpace(replay.CurrentRunId))
            replay.CurrentRunId = $"history-{++replay.TurnIndex}";

        replay.AssistantBuffer.Append(text);
    }

    private static void CaptureReplayToolCall(ReplayHistoryState replay, JsonElement update)
    {
        // Do NOT flush the assistant buffer here: replayed turns must keep the
        // live persistence shape (one assistant message per turn), so tool
        // events only record data and everything is emitted at the turn end.
        ApplyReplayToolUpdate(replay, update);
    }

    private static void CaptureReplayToolUpdate(ReplayHistoryState replay, JsonElement update)
    {
        ApplyReplayToolUpdate(replay, update);
    }

    private static void ApplyReplayToolUpdate(ReplayHistoryState replay, JsonElement update)
    {
        if (string.IsNullOrWhiteSpace(replay.CurrentRunId))
            replay.CurrentRunId = $"history-{++replay.TurnIndex}";

        var toolCallId = GetString(update, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
            toolCallId = "history-tool-" + Guid.NewGuid().ToString("N");

        var name = ReadToolName(update);
        if (!replay.ToolNames.ContainsKey(toolCallId))
            replay.ToolNames[toolCallId] = name;
        else if (string.Equals(replay.ToolNames[toolCallId], "Tool", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(name, "Tool", StringComparison.OrdinalIgnoreCase))
            replay.ToolNames[toolCallId] = name;

        var presentTitle = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(presentTitle))
            replay.ToolNames[toolCallId] = presentTitle;

        var hasRawInput = update.TryGetProperty("rawInput", out _);
        if (hasRawInput)
        {
            var input = FormatToolInput(update);
            replay.ToolInputs[toolCallId] = input;
            replay.ToolSummaries[toolCallId] = BuildToolSummary(replay.ToolNames[toolCallId], input, update);
        }
        else if (!replay.ToolInputs.ContainsKey(toolCallId))
        {
            replay.ToolInputs[toolCallId] = "";
        }

        if (!string.IsNullOrWhiteSpace(presentTitle) || !replay.ToolSummaries.ContainsKey(toolCallId))
        {
            replay.ToolSummaries[toolCallId] = BuildToolSummary(
                replay.ToolNames[toolCallId],
                replay.ToolInputs.GetValueOrDefault(toolCallId, ""),
                update);
        }

        if (!replay.ToolRunIds.ContainsKey(toolCallId))
            replay.ToolRunIds[toolCallId] = replay.CurrentRunId;
        if (!replay.ToolOrder.Contains(toolCallId))
            replay.ToolOrder.Add(toolCallId);

        var status = GetString(update, "status");
        var isTerminalStatus = status is "completed" or "failed";
        var hasContent = update.TryGetProperty("content", out _);
        var hasRawOutput = update.TryGetProperty("rawOutput", out _);
        var terminalChunk = ReadTerminalOutputChunk(update);

        if (hasContent || hasRawOutput)
        {
            var displayOutput = FormatContentAndRawOutput(update);
            var hasCommittedInput = replay.ToolInputs.TryGetValue(toolCallId, out var existingInput)
                && !string.IsNullOrWhiteSpace(existingInput);
            if (!isTerminalStatus
                && !hasRawInput
                && !hasCommittedInput
                && IsPendingParamSnapshot(displayOutput))
            {
                // Keep the latest pending snapshot out of ToolOutputs until
                // rawInput or a terminal formal result arrives.
            }
            else
            {
                replay.ToolOutputs[toolCallId] = displayOutput;
            }
        }
        else if (!string.IsNullOrEmpty(terminalChunk))
        {
            replay.ToolOutputs[toolCallId] = replay.ToolOutputs.TryGetValue(toolCallId, out var existing)
                ? existing + terminalChunk
                : terminalChunk;
        }
        else if (!replay.ToolOutputs.ContainsKey(toolCallId))
        {
            replay.ToolOutputs[toolCallId] = "";
        }

        if (isTerminalStatus)
            replay.ToolStatuses[toolCallId] = status;
    }

    private static void UpsertReplayPlan(ReplayHistoryState replay, JsonElement update)
    {
        if (string.IsNullOrWhiteSpace(replay.CurrentRunId))
            replay.CurrentRunId = $"history-{++replay.TurnIndex}";

        var entries = ReadPlanEntries(update);
        var text = FormatPlanText(entries, update);
        if (entries.Count == 0 && string.IsNullOrWhiteSpace(text))
            return;
        UpsertPlanMessage(replay.Messages, replay.CurrentRunId, entries, text);
    }

    private static void FinishReplayHistory(ReplayHistoryState replay)
    {
        FlushReplayTurn(replay);
    }

    /// <summary>
    /// Emits everything buffered for the current replayed turn in the live
    /// persistence order — thinking first, then the turn's tools in the order
    /// they appeared (unfinished ones included, so they stay in their own
    /// turn instead of piling up at the end of the whole replay), then the
    /// single combined assistant message.
    /// </summary>
    private static void FlushReplayTurn(ReplayHistoryState replay)
    {
        FlushReplayThinking(replay);
        foreach (var toolCallId in replay.ToolOrder.ToArray())
            EmitReplayTool(replay, toolCallId);
        replay.ToolOrder.Clear();
        FlushReplayAssistant(replay);
    }

    private static void FlushReplayThinking(ReplayHistoryState replay)
    {
        var text = replay.ThinkingBuffer.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            replay.ThinkingBuffer.Clear();
            return;
        }

        replay.Messages.Add(new AgentMessage
        {
            Role = "thinking",
            Text = text,
            RunId = replay.CurrentRunId,
            CreatedAt = DateTimeOffset.Now
        });
        replay.ThinkingBuffer.Clear();
    }

    private static void FlushReplayAssistant(ReplayHistoryState replay)
    {
        var text = replay.AssistantBuffer.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            replay.AssistantBuffer.Clear();
            return;
        }

        replay.Messages.Add(new AgentMessage
        {
            Role = "assistant",
            Text = text,
            RunId = replay.CurrentRunId,
            CreatedAt = DateTimeOffset.Now
        });
        replay.AssistantBuffer.Clear();
    }

    private static void EmitReplayTool(ReplayHistoryState replay, string toolCallId)
    {
        var name = replay.ToolNames.TryGetValue(toolCallId, out var n) ? n : "Tool";
        var input = replay.ToolInputs.TryGetValue(toolCallId, out var i) ? i : "";
        var output = replay.ToolOutputs.TryGetValue(toolCallId, out var o) ? o : "";
        var summary = replay.ToolSummaries.TryGetValue(toolCallId, out var s) ? s : name;
        var runId = replay.ToolRunIds.TryGetValue(toolCallId, out var r) ? r : replay.CurrentRunId;
        var status = replay.ToolStatuses.TryGetValue(toolCallId, out var st) ? st : "done";

        replay.Messages.Add(new AgentMessage
        {
            Role = "tool",
            Text = input,
            Name = name,
            RunId = runId,
            ToolCallId = toolCallId,
            ToolInput = input,
            ToolOutput = output,
            ToolStatus = status,
            Summary = summary,
            CreatedAt = DateTimeOffset.Now
        });

        replay.ToolNames.Remove(toolCallId);
        replay.ToolInputs.Remove(toolCallId);
        replay.ToolOutputs.Remove(toolCallId);
        replay.ToolSummaries.Remove(toolCallId);
        replay.ToolRunIds.Remove(toolCallId);
        replay.ToolStatuses.Remove(toolCallId);
    }

    private async Task FinishToolAsync(string toolCallId, string status)
    {
        AcpToolSnapshot? snapshot;
        lock (_sessionMutationSync)
        {
            snapshot = _toolState.Complete(toolCallId, status);
            if (snapshot != null)
            {
                AddToolMessageCore(
                    snapshot.Input,
                    snapshot.Name,
                    _currentRunId,
                    snapshot.ToolCallId,
                    snapshot.Output,
                    snapshot.Status,
                    snapshot.Summary);
            }
        }

        if (snapshot == null)
            return;

        await SendToolUpdatedSnapshotAsync(snapshot).ConfigureAwait(false);
        await _bridgeService.SendEventAsync(new
        {
            type = "tool_finished",
            runId = _currentRunId,
            toolCallId = snapshot.ToolCallId,
            summary = snapshot.Summary,
            status = snapshot.Status
        }).ConfigureAwait(false);
    }

    private Task SendAvailableCommandsAsync(JsonElement update)
    {
        if (!update.TryGetProperty("availableCommands", out var commands) || commands.ValueKind != JsonValueKind.Array)
            return Task.CompletedTask;

        var items = commands.EnumerateArray()
            .Select(command => new
            {
                name = NormalizeSlashCommand(GetString(command, "name")),
                description = GetString(command, "description")
            })
            .Where(command => !string.IsNullOrWhiteSpace(command.name))
            .Where(command => _provider.IsCommandVisible(command.name!))
            .GroupBy(command => command.name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        lock (_commandLock)
        {
            _availableAgentCommands.Clear();
            foreach (var item in items)
                _availableAgentCommands[item.name!] = item.name!;
            _agentCommandsReady = true;
        }

        return _bridgeService.SendEventAsync(new
        {
            type = "agent_commands",
            ready = true,
            commands = items
        });
    }

    private Task HandleUsageUpdateAsync(JsonElement update, bool persist = true)
    {
        var used = TryGetLong(update, "used");
        if (used is null or < 0)
            return Task.CompletedTask;

        long? contextWindowTokens;
        decimal? contextCostAmount;
        string? contextCostCurrency;
        lock (_sessionMutationSync)
        {
            _currentThread.ContextUsedTokens = used.Value;
            var size = TryGetLong(update, "size");
            _currentThread.ContextWindowTokens = size is > 0 ? size : null;
            if (update.TryGetProperty("cost", out var cost))
            {
                var amount = TryGetDecimal(cost, "amount");
                var currency = GetString(cost, "currency");
                _currentThread.ContextCostAmount = amount is >= 0 && !string.IsNullOrWhiteSpace(currency) ? amount : null;
                _currentThread.ContextCostCurrency = _currentThread.ContextCostAmount.HasValue ? currency : null;
            }

            if (persist)
                SaveCurrentThreadCore();
            contextWindowTokens = _currentThread.ContextWindowTokens;
            contextCostAmount = _currentThread.ContextCostAmount;
            contextCostCurrency = _currentThread.ContextCostCurrency;
        }
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_usage_update",
            contextUsedTokens = used.Value,
            contextWindowTokens,
            contextCostAmount,
            contextCostCurrency
        });
    }

    private async Task SetModeAsync(string modeId)
    {
        await EnsureAcpSessionAsync(createIfMissing: true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_acpSessionId))
            return;

        await _transport!.SendRequestAsync("session/set_mode", new
        {
            sessionId = _acpSessionId,
            modeId
        }, TimeSpan.FromSeconds(15), _serviceLifetimeCts.Token).ConfigureAwait(false);

        lock (_sessionMutationSync)
        {
            _currentModeId = modeId;
            _currentThread.ModeId = modeId;
            SaveCurrentThreadCore();
        }
        await SendModesAsync().ConfigureAwait(false);
    }

    private async Task SetConfigOptionAsync(string configId, object value, bool isBoolean)
    {
        await EnsureAcpSessionAsync(createIfMissing: true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_acpSessionId))
            return;

        var parameters = isBoolean
            ? new Dictionary<string, object?>
            {
                ["sessionId"] = _acpSessionId,
                ["configId"] = configId,
                ["type"] = "boolean",
                ["value"] = value
            }
            : new Dictionary<string, object?>
            {
                ["sessionId"] = _acpSessionId,
                ["configId"] = configId,
                ["value"] = value
            };
        var result = await _transport!.SendRequestAsync("session/set_config_option", parameters,
            TimeSpan.FromSeconds(15), _serviceLifetimeCts.Token).ConfigureAwait(false);

        lock (_sessionMutationSync)
        {
            CaptureConfigOptions(result);
            if (configId == "mode" && value is string modeId)
                _currentModeId = modeId;
            _currentThread.ModeId = _currentModeId;
            SaveCurrentThreadCore();
        }
        await Task.WhenAll(SendConfigOptionsAsync(), SendModesAsync()).ConfigureAwait(false);
    }

    private void CaptureModes(JsonElement result)
    {
        lock (_sessionMutationSync)
        {
            if (!result.TryGetProperty("modes", out var modes)
                || modes.ValueKind != JsonValueKind.Object)
            {
                _modes = Array.Empty<AcpMode>();
                _currentModeId = null;
                return;
            }

            _currentModeId = GetString(modes, "currentModeId");
            if (modes.TryGetProperty("availableModes", out var availableModes)
                && availableModes.ValueKind == JsonValueKind.Array)
            {
                _modes = availableModes.EnumerateArray()
                    .Select(mode => new AcpMode
                    {
                        Id = GetString(mode, "id"),
                        Name = GetString(mode, "name"),
                        Description = GetString(mode, "description")
                    })
                    .Where(mode => !string.IsNullOrWhiteSpace(mode.Id))
                    .ToArray();
            }

            _currentThread.ModeId = _currentModeId;
        }
    }

    private void CaptureConfigOptions(JsonElement result)
    {
        lock (_sessionMutationSync)
        {
            if (!result.TryGetProperty("configOptions", out var configOptions)
                || configOptions.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            _configOptions = configOptions.EnumerateArray()
                .Select(option => new AcpConfigOption
                {
                    Id = GetString(option, "id"),
                    Name = GetString(option, "name"),
                    Description = GetString(option, "description"),
                    Category = GetString(option, "category"),
                    Type = GetString(option, "type"),
                    CurrentValue = GetString(option, "currentValue"),
                    BooleanValue = TryGetBoolean(option, "currentValue"),
                    Options = ReadConfigOptionValues(option)
                })
                .Where(option => !string.IsNullOrWhiteSpace(option.Id)
                                 && ((option.Type == "select" && option.Options.Count > 0)
                                     || (option.Type == "boolean" && option.BooleanValue.HasValue)))
                .ToArray();

            var modeOption = _configOptions.FirstOrDefault(option => option.Id == "mode");
            if (!string.IsNullOrWhiteSpace(modeOption?.CurrentValue))
            {
                _currentModeId = modeOption.CurrentValue;
                _currentThread.ModeId = _currentModeId;
            }
        }
    }

    private Task SendSessionReadyAsync()
    {
        return Task.WhenAll(
            _bridgeService.SendEventAsync(new { type = "agent_ready", sessionId = _acpSessionId ?? "" }),
            SendModesAsync(),
            SendConfigOptionsAsync(),
            PublishStateAsync());
    }

    private Task SendModesAsync()
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_modes",
            currentModeId = _currentModeId ?? "",
            modes = _modes.Select(mode => new
            {
                id = mode.Id,
                name = mode.Name,
                description = mode.Description
            }).ToArray()
        });
    }

    private Task SendConfigOptionsAsync()
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "agent_config_options",
            options = _configOptions.Select(option => new
            {
                id = option.Id,
                name = option.Name,
                description = option.Description,
                category = option.Category,
                type = option.Type,
                currentValue = option.Type == "boolean" ? (object?)option.BooleanValue : option.CurrentValue,
                options = option.Options.Select(value => new
                {
                    value = value.Value,
                    name = value.Name,
                    description = value.Description
                }).ToArray()
            }).ToArray()
        });
    }

    private Task SendAssistantTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        _assistantBuffer.Append(text);
        return _bridgeService.SendEventAsync(new { type = "assistant_delta", text });
    }

    private Task SendThinkingTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        _thinkingBuffer.Append(text);
        return _bridgeService.SendEventAsync(new { type = "thinking_delta", text, runId = _currentRunId });
    }

    private async Task HandlePlanUpdateAsync(JsonElement update)
    {
        var runId = string.IsNullOrWhiteSpace(_currentRunId)
            ? "plan-" + Guid.NewGuid().ToString("N")
            : _currentRunId;
        var entries = ReadPlanEntries(update);
        var text = FormatPlanText(entries, update);
        // Tolerant plan handling: a provider may send a full plan document
        // without a checklist. Only a payload with neither entries nor any
        // readable text is dropped.
        if (entries.Count == 0 && string.IsNullOrWhiteSpace(text))
            return;

        lock (_sessionMutationSync)
        {
            UpsertPlanMessage(_currentThread.Messages, runId, entries, text);
            SaveCurrentThreadCore();
        }

        await _bridgeService.SendEventAsync(new
        {
            type = "plan_update",
            runId,
            text,
            entries = entries.Select(entry => new
            {
                content = entry.Content,
                status = entry.Status,
                priority = entry.Priority
            }).ToArray()
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes the current run's streamed thinking/assistant buffers into the
    /// thread transcript, but only when <paramref name="runId"/>/<paramref name="cts"/>
    /// still own the run. The ownership check prevents an orphaned old run task
    /// (a task whose run was already superseded by a newer prompt) from writing
    /// stale text into the shared buffers of the new run. The buffers are read
    /// and cleared atomically under <c>_runLock</c>, so repeat calls are
    /// naturally idempotent (the second call sees empty buffers) and the
    /// persistence IO happens outside the lock.
    /// </summary>
    private Task FinalizeRunOutputAsync(string? runId, CancellationTokenSource? cts)
    {
        string thinkingText;
        string assistantText;
        lock (_runLock)
        {
            if (_currentRunId != runId || !ReferenceEquals(_runCts, cts))
                return Task.CompletedTask;
            thinkingText = _thinkingBuffer.ToString();
            _thinkingBuffer.Clear();
            assistantText = _assistantBuffer.ToString();
            _assistantBuffer.Clear();
        }
        PersistRunOutput(runId, thinkingText, assistantText);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Synchronous drain core shared by <see cref="FinalizeRunOutputAsync"/> and
    /// <see cref="Dispose"/>. Writes the given thinking/assistant text under the
    /// explicit <paramref name="runId"/> (never re-reading <c>_currentRunId</c>,
    /// which a finally block may already have cleared) and saves the thread.
    /// Sends no bridge events, so it is safe to call while tearing down.
    /// </summary>
    private void PersistRunOutput(string? runId, string thinkingText, string assistantText)
    {
        lock (_sessionMutationSync)
        {
            var changed = false;
            if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                _currentThread.Messages.Add(new AgentMessage
                {
                    Role = "thinking",
                    Text = thinkingText,
                    RunId = runId,
                    CreatedAt = DateTimeOffset.Now
                });
                ThinkingMessageNormalizer.Normalize(_currentThread.Messages);
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                _currentThread.Messages.Add(new AgentMessage
                {
                    Role = "assistant",
                    Text = assistantText,
                    RunId = runId,
                    CreatedAt = DateTimeOffset.Now
                });
                changed = true;
            }

            if (changed)
                SaveCurrentThreadCore();
        }
    }

    private void AddMessage(string role, string text, string? name = null, IReadOnlyList<AgentAttachment>? attachments = null)
    {
        lock (_sessionMutationSync)
        {
            if (role == "user" && _currentThread.Messages.Count == 0)
                _currentThread.Title = BuildMessageTitle(text);

            if (role == "user" && attachments is { Count: > 0 })
                _currentThread.ContainsImages = true;

            _currentThread.Messages.Add(new AgentMessage
            {
                Role = role,
                Text = text,
                Name = name,
                RunId = role is "user" or "assistant" or "thinking" ? _currentRunId : null,
                Attachments = attachments?.Select(CloneAttachment).ToList(),
                CreatedAt = DateTimeOffset.Now
            });
            SaveCurrentThreadCore();
        }
    }

    /// <summary>Requires <see cref="_sessionMutationSync"/>.</summary>
    private void AddToolMessageCore(string text, string name, string? runId,
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
        SaveCurrentThreadCore();
    }

    private void SaveCurrentThread()
    {
        lock (_sessionMutationSync)
        {
            SaveCurrentThreadCore();
        }
    }

    private void SaveCurrentThreadCore()
    {
        if (IsBoundProviderThread(_currentThread))
        {
            _currentThread.Cwd = _workingDirectory;
            _currentThread.Provider = _provider.Descriptor.Key;
            _currentThread.AcpSessionId = _acpSessionId ?? _sessionIdForTransportRecovery;
            _currentThread.ModeId = _currentModeId;
            _currentThread.AdapterVersion = _adapterVersion;
        }
        _threadStore.SaveThread(_currentThread);
    }

    private Task SendRunFailedAsync(string text, string? runId = null)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "run_failed",
            text,
            runId,
            visionContextHint = _currentThread.ContainsImages
                ? "提示：当前对话曾发送过图片，这个错误可能是因为当前模型或供应商不支持图片上下文，或当前模型不是多模态模型导致，建议切换至多模态模型。"
                : ""
        });
    }

    private Task SendResumeFailedAsync(string message, string? detail = null)
    {
        return _bridgeService.SendEventAsync(new
        {
            type = "resume_failed",
            message,
            detail = string.IsNullOrWhiteSpace(detail) ? null : detail
        });
    }

    private void ApplyThread(AgentThread thread)
    {
        lock (_sessionMutationSync)
        {
            if (DocumentDecisionSnapshotMerger.InterruptPending(thread))
                _threadStore.SaveThread(thread);

            ClearAvailableAgentCommands();
            EnsureImageContextFlag(thread);
            _workingDirectory = Directory.Exists(thread.Cwd) ? thread.Cwd : ResolveWorkspaceRoot();
            _acpSessionId = null;
            _sessionIdForTransportRecovery = null;
            _transportRecoveryRequired = false;
            _currentModeId = thread.ModeId;
            _adapterVersion = thread.AdapterVersion;
            _status = IsBoundProviderThread(thread) ? "ready" : "transcript_only";
            _thinkingBuffer.Clear();
            _assistantBuffer.Clear();
            _toolState.Clear();
            _currentRunId = null;
        }
    }

    private void EnsureImageContextFlag(AgentThread thread)
    {
        if (thread.ContainsImages)
            return;

        if (!thread.Messages.Any(message => message.Attachments is { Count: > 0 }))
            return;

        thread.ContainsImages = true;
        _threadStore.SaveThread(thread);
    }

    private static bool IsEmptyAgentDraft(AgentThread thread)
    {
        return string.IsNullOrWhiteSpace(thread.ClaudeSessionId)
            && string.IsNullOrWhiteSpace(thread.AcpSessionId)
            && thread.Messages.Count == 0;
    }

    private bool IsBoundProviderThread(AgentThread thread)
    {
        return ReferenceEquals(_providerRegistry.Find(thread.Provider), _provider);
    }

    private static string BuildUnsupportedProviderMessage(string? providerKey)
    {
        var displayKey = string.IsNullOrWhiteSpace(providerKey) ? "(missing)" : providerKey;
        return $"This thread belongs to an unsupported Agent provider ({displayKey}). Showing local transcript only.";
    }

    private Task SendThreadLoadedAsync(bool clear, bool selectPlan = false)
    {
        return _bridgeService.SendEventAsync(
            AgentThreadBridgePayload.ThreadLoaded(_currentThread, clear, selectPlan));
    }

    private IReadOnlyList<AgentAttachment> ResolvePromptAttachments(IReadOnlyList<string> attachmentIds)
    {
        if (attachmentIds.Count == 0)
            return Array.Empty<AgentAttachment>();

        if (!_supportsImage)
            throw new InvalidOperationException("Current ACP Agent does not support image input.");

        if (attachmentIds.Count > MaxPromptImages)
            throw new InvalidOperationException($"You can send at most {MaxPromptImages} images at once.");

        var attachments = _threadStore.LoadAttachments(_currentThread.ThreadId, attachmentIds);
        if (attachments.Count != attachmentIds.Count)
            throw new InvalidOperationException("One or more image attachments were not found. Remove them and try again.");

        var totalBytes = attachments.Sum(attachment => attachment.Size);
        if (totalBytes > MaxPromptImageBytes)
            throw new InvalidOperationException("Images in a single message must total 50MB or less.");

        return attachments;
    }

    private static object[] BuildPromptBlocks(string prompt, IReadOnlyList<AgentAttachment> attachments)
    {
        var blocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(prompt))
            blocks.Add(new { type = "text", text = prompt });

        foreach (var attachment in attachments)
        {
            blocks.Add(new
            {
                type = "image",
                mimeType = attachment.MimeType,
                data = Convert.ToBase64String(File.ReadAllBytes(attachment.Path)),
                uri = attachment.Uri
            });
        }

        return blocks.ToArray();
    }

    private static object ToAttachmentPayload(AgentAttachment attachment)
    {
        return new
        {
            id = attachment.Id,
            fileName = attachment.FileName,
            mimeType = attachment.MimeType,
            size = attachment.Size,
            url = attachment.Url,
            uri = attachment.Uri,
            createdAt = attachment.CreatedAt.ToString("u")
        };
    }

    private static AgentAttachment CloneAttachment(AgentAttachment attachment)
    {
        return new AgentAttachment
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            MimeType = attachment.MimeType,
            Size = attachment.Size,
            Path = attachment.Path,
            Url = attachment.Url,
            Uri = attachment.Uri,
            CreatedAt = attachment.CreatedAt
        };
    }

    private string EnsureAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Empty path is not allowed.");

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(_workingDirectory);
        if (!IsWithin(fullPath, root))
            throw new InvalidOperationException($"Path is outside the current workspace: {fullPath}");

        return fullPath;
    }

    private string EnsureAllowedDirectory(string path)
    {
        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"Directory does not exist: {directory}");

        return EnsureAllowedPath(directory);
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindRepositoryRoots()
    {
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(candidate);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PSX.csproj")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    yield return directory.FullName;

                directory = directory.Parent;
            }
        }
    }

    private static string ResolveWorkspaceRoot()
    {
        foreach (var root in FindRepositoryRoots())
            return root;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string BuildMessageTitle(string text)
    {
        var compact = string.Join(' ', text.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(compact))
            return "Agent Chat";

        return compact.Length <= 48 ? compact : compact[..48] + "...";
    }

    private static string ReadToolName(JsonElement update)
    {
        var metaName = ReadNestedString(update, "_meta", "claudeCode", "toolName");
        if (!string.IsNullOrWhiteSpace(metaName))
            return metaName;

        var title = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        var kind = GetString(update, "kind");
        return string.IsNullOrWhiteSpace(kind) ? "Tool" : kind;
    }

    private static string FormatToolInput(JsonElement update)
    {
        if (!update.TryGetProperty("rawInput", out var rawInput))
            return "";

        return rawInput.ValueKind == JsonValueKind.String
            ? rawInput.GetString() ?? ""
            : rawInput.GetRawText();
    }

    private static string ReadTerminalOutputChunk(JsonElement update)
    {
        var terminalOutput = ReadNestedElement(update, "_meta", "terminal_output");
        return terminalOutput.HasValue
            ? GetString(terminalOutput.Value, "data")
            : "";
    }

    /// <summary>
    /// Prefer readable <c>content</c>; fall back to <c>rawOutput</c> only when
    /// content is empty. Never concatenate both — Claude Read often ships the
    /// same result as fenced content plus plain rawOutput.
    /// </summary>
    private static string FormatContentAndRawOutput(JsonElement update)
    {
        var contentText = FormatContentBlocks(update);
        if (!string.IsNullOrWhiteSpace(contentText))
            return contentText;

        if (!update.TryGetProperty("rawOutput", out var rawOutput))
            return "";

        if (rawOutput.ValueKind == JsonValueKind.String)
            return rawOutput.GetString() ?? "";
        if (rawOutput.ValueKind != JsonValueKind.Null)
            return rawOutput.GetRawText();
        return "";
    }

    private static string FormatContentBlocks(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            var itemType = GetString(item, "type");
            if (itemType == "content"
                && item.TryGetProperty("content", out var block)
                && GetString(block, "type") == "text")
            {
                parts.Add(GetString(block, "text"));
            }
            else if (itemType == "text")
            {
                parts.Add(GetString(item, "text"));
            }
            else if (itemType == "diff")
            {
                parts.Add(item.GetRawText());
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatToolOutput(JsonElement update)
    {
        var parts = new List<string>();
        var terminal = ReadTerminalOutputChunk(update);
        if (!string.IsNullOrWhiteSpace(terminal))
            parts.Add(terminal);

        var body = FormatContentAndRawOutput(update);
        if (!string.IsNullOrWhiteSpace(body))
            parts.Add(body);

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsPendingParamSnapshot(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static string BuildToolSummary(string name, string input, JsonElement update)
    {
        var title = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(title))
            return title.Length <= 100 ? title : title[..97] + "...";

        if (string.IsNullOrWhiteSpace(input))
            return name;

        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            var detail = GetString(root, "file_path");
            if (string.IsNullOrWhiteSpace(detail))
                detail = GetString(root, "path");
            if (string.IsNullOrWhiteSpace(detail))
                detail = GetString(root, "command");
            if (string.IsNullOrWhiteSpace(detail))
                detail = GetString(root, "description");

            if (string.IsNullOrWhiteSpace(detail))
                return name;

            var summary = name + " " + detail;
            return summary.Length <= 100 ? summary : summary[..97] + "...";
        }
        catch
        {
            return name;
        }
    }

    private static void UpsertPlanMessage(List<AgentMessage> messages, string? runId, IReadOnlyList<AgentPlanEntry> entries, string text)
    {
        var effectiveRunId = string.IsNullOrWhiteSpace(runId) ? "plan" : runId;
        var index = messages.FindLastIndex(message =>
            message.Role == "plan" && string.Equals(message.RunId, effectiveRunId, StringComparison.Ordinal));

        var planMessage = new AgentMessage
        {
            Role = "plan",
            Name = "Plan",
            Text = text,
            RunId = effectiveRunId,
            PlanEntries = entries.ToList(),
            CreatedAt = DateTimeOffset.Now
        };

        if (index >= 0)
        {
            planMessage.CreatedAt = messages[index].CreatedAt;
            messages[index] = planMessage;
        }
        else
        {
            messages.Add(planMessage);
        }
    }

    private void UpsertDocumentDecisionMessage(
        PermissionPresentation presentation,
        string decisionSnapshotId,
        string requestId,
        string toolCallId,
        string title,
        string documentText,
        IReadOnlyList<AgentDecisionOption> options,
        string state)
    {
        lock (_sessionMutationSync)
        {
            var role = GetDocumentDecisionRole(presentation);
            var index = -1;
            if (!string.IsNullOrWhiteSpace(decisionSnapshotId))
            {
                index = _currentThread.Messages.FindLastIndex(message =>
                    message.Role == role
                    && string.Equals(message.DecisionSnapshotId, decisionSnapshotId, StringComparison.Ordinal));
            }

            if (index < 0)
            {
                index = _currentThread.Messages.FindLastIndex(message =>
                    message.Role == role
                    && IsActiveDocumentDecisionState(message.DecisionState)
                    && string.Equals(message.RequestId, requestId, StringComparison.Ordinal));
            }

            if (index < 0 && !string.IsNullOrWhiteSpace(toolCallId))
            {
                index = _currentThread.Messages.FindLastIndex(message =>
                    message.Role == "tool"
                    && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal)
                    && string.Equals(message.RunId, _currentRunId, StringComparison.Ordinal));
            }

            var existing = index >= 0 ? _currentThread.Messages[index] : null;
            var message = new AgentMessage
            {
                Role = role,
                Name = title,
                Text = documentText,
                RunId = existing?.RunId ?? _currentRunId,
                ToolCallId = toolCallId,
                RequestId = requestId,
                DecisionSnapshotId = decisionSnapshotId,
                DecisionState = state,
                DecisionOptions = options.Select(DocumentDecisionSnapshotMerger.CloneOption).ToList(),
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.Now
            };

            if (index >= 0)
                _currentThread.Messages[index] = message;
            else
                _currentThread.Messages.Add(message);

            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                _currentThread.Messages.RemoveAll(candidate =>
                    !ReferenceEquals(candidate, message)
                    && candidate.Role == "tool"
                    && string.Equals(candidate.ToolCallId, toolCallId, StringComparison.Ordinal)
                    && string.Equals(candidate.RunId, message.RunId, StringComparison.Ordinal));
            }

            SaveCurrentThreadCore();
        }
    }

    private void UpdateDocumentDecisionState(
        string requestId,
        PermissionPresentation presentation,
        string state,
        string? selectedOptionId = null,
        string? decisionSnapshotId = null)
    {
        lock (_sessionMutationSync)
        {
            var role = GetDocumentDecisionRole(presentation);
            AgentMessage? message = null;
            if (!string.IsNullOrWhiteSpace(decisionSnapshotId))
            {
                message = _currentThread.Messages.FindLast(candidate =>
                    candidate.Role == role
                    && string.Equals(candidate.DecisionSnapshotId, decisionSnapshotId, StringComparison.Ordinal));
            }

            if (message == null)
            {
                message = _currentThread.Messages.FindLast(candidate =>
                    candidate.Role == role
                    && string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal)
                    && IsActiveDocumentDecisionState(candidate.DecisionState));
            }

            if (message == null)
                return;

            message.DecisionState = state;
            message.SelectedOptionId = selectedOptionId;
            SaveCurrentThreadCore();
        }
    }

    private static bool IsActiveDocumentDecisionState(string? state) =>
        string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "sending", StringComparison.OrdinalIgnoreCase);

    private static string GetDocumentDecisionRole(PermissionPresentation presentation)
    {
        return presentation switch
        {
            PermissionPresentation.ModeTransition => "mode_transition",
            PermissionPresentation.Document => "document_permission",
            _ => throw new ArgumentOutOfRangeException(nameof(presentation), presentation, "Only document presentations have transcript snapshots.")
        };
    }

    private static List<AgentPlanEntry> ReadPlanEntries(JsonElement update)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new List<AgentPlanEntry>();

        return entries.EnumerateArray()
            .Select(entry => new AgentPlanEntry
            {
                Content = ReadPlanEntryContent(entry),
                Status = GetString(entry, "status"),
                Priority = GetString(entry, "priority")
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Content))
            .ToList();
    }

    private static string FormatPlanText(IReadOnlyList<AgentPlanEntry> entries, JsonElement fallback)
    {
        if (entries.Count == 0)
            return ReadPlanDocumentText(fallback);

        return string.Join(Environment.NewLine, entries.Select(entry =>
        {
            var status = string.IsNullOrWhiteSpace(entry.Status) ? "" : $"[{entry.Status}] ";
            return "- " + status + entry.Content;
        }));
    }

    /// <summary>
    /// Extracts a human-readable plan document from a plan update that has no
    /// checklist entries. Providers differ in where they put the document
    /// (plain string fields or ACP content blocks); anything unreadable
    /// returns an empty string so the caller can drop the update instead of
    /// surfacing raw JSON.
    /// </summary>
    private static string ReadPlanDocumentText(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var propertyName in new[] { "text", "markdown", "document", "plan", "description" })
        {
            if (update.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        if (!update.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()?.Trim() ?? "";

        if (content.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var raw = item.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                    parts.Add(raw.Trim());
                continue;
            }

            var itemType = GetString(item, "type");
            string? blockText = null;
            if (itemType == "text")
            {
                blockText = GetString(item, "text");
            }
            else if (itemType == "content"
                && item.TryGetProperty("content", out var block)
                && GetString(block, "type") == "text")
            {
                blockText = GetString(block, "text");
            }

            if (!string.IsNullOrWhiteSpace(blockText))
                parts.Add(blockText.Trim());
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static string ReadPlanEntryContent(JsonElement entry)
    {
        var content = GetString(entry, "content");
        if (!string.IsNullOrWhiteSpace(content))
            return content;

        return GetString(entry, "title");
    }

    private static string? NormalizeSlashCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static IReadOnlyList<AcpConfigOptionValue> ReadConfigOptionValues(JsonElement option)
    {
        if (!option.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return Array.Empty<AcpConfigOptionValue>();

        var values = new List<AcpConfigOptionValue>();
        foreach (var item in options.EnumerateArray())
        {
            if (item.TryGetProperty("options", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(ReadConfigOptionValueArray(nested));
            }
            else
            {
                var value = ReadConfigOptionValue(item);
                if (value != null)
                    values.Add(value);
            }
        }

        return values;
    }

    private static IEnumerable<AcpConfigOptionValue> ReadConfigOptionValueArray(JsonElement options)
    {
        foreach (var item in options.EnumerateArray())
        {
            var value = ReadConfigOptionValue(item);
            if (value != null)
                yield return value;
        }
    }

    private static AcpConfigOptionValue? ReadConfigOptionValue(JsonElement item)
    {
        var value = GetString(item, "value");
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var name = GetString(item, "name");
        return new AcpConfigOptionValue
        {
            Value = value,
            Name = string.IsNullOrWhiteSpace(name) ? value : name,
            Description = GetString(item, "description")
        };
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number)
                ? number
                : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean()
                : null;
    }

    private static string ReadNestedString(JsonElement element, params string[] path)
    {
        var current = ReadNestedElement(element, path);
        return current.HasValue && current.Value.ValueKind == JsonValueKind.String
            ? current.Value.GetString() ?? ""
            : "";
    }

    private static bool? ReadNestedBool(JsonElement element, params string[] path)
    {
        var current = ReadNestedElement(element, path);
        return current.HasValue && current.Value.ValueKind == JsonValueKind.True ? true
            : current.HasValue && current.Value.ValueKind == JsonValueKind.False ? false
            : null;
    }

    /// <summary>
    /// ACP declares resume support through the presence of
    /// <c>agentCapabilities.sessionCapabilities.resume</c> — the spec shows an
    /// empty object, and a bare <c>true</c> is also accepted. Absence,
    /// <c>null</c> and <c>false</c> all mean unsupported, so this is an
    /// existence check rather than the bool-only reader.
    /// </summary>
    internal static bool SupportsSessionResume(JsonElement initResult)
    {
        var resume = ReadNestedElement(initResult, "agentCapabilities", "sessionCapabilities", "resume");
        return resume.HasValue
            && resume.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True;
    }

    private static JsonElement? ReadNestedElement(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                return null;
        }

        return current;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => JsonElementToObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue) ? doubleValue : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _bridgeService.UserMessageSubmitted -= OnUserMessageSubmitted;
        _bridgeService.CommandReceived -= OnCommandReceived;
        _bridgeService.AttachmentUploadReceived -= OnAttachmentUploadReceived;
        _runtime.StatusChanged -= OnRuntimeStatusChanged;
        _runtimeCoordinator.UpdateStatusChanged -= OnRuntimeUpdateStatusChanged;
        try { _serviceLifetimeCts.Cancel(); } catch { }
        try { _runCts?.Cancel(); } catch { }
        try { _runRequestCts?.Cancel(); } catch { }
        try { _runtimeInstallCts?.Cancel(); } catch { }

        foreach (var item in _pendingPermissions.ToArray())
        {
            if (_pendingPermissions.TryRemove(item.Key, out var pending))
                pending.Completion.TrySetResult("__cancelled__");
        }

        foreach (var item in _pendingElicitations.ToArray())
        {
            if (_pendingElicitations.TryRemove(item.Key, out var pending))
                pending.Completion.TrySetResult("{\"action\":\"cancel\"}");
        }

        _runtimeInstallCts?.Dispose();
        _runCts?.Dispose();
        _runRequestCts?.Dispose();

        // Best-effort flush of any in-flight streamed output before the transport
        // is killed. The run task's finally is short-circuited by _disposed, so
        // this is the only chance to persist a partial turn on shutdown. No
        // ownership check (the service is going away) and no bridge events.
        try
        {
            string thinkingText;
            string assistantText;
            lock (_runLock)
            {
                thinkingText = _thinkingBuffer.ToString();
                _thinkingBuffer.Clear();
                assistantText = _assistantBuffer.ToString();
                _assistantBuffer.Clear();
            }
            PersistRunOutput(_currentRunId, thinkingText, assistantText);
        }
        catch { }

        var lockTaken = _transportLock.Wait(TransportResetLockBudget);
        try
        {
            if (lockTaken)
            {
                if (_transport != null)
                    ResetTransportCore(_transport, _transportGeneration);
            }
            else
            {
                // Could not acquire the lock within the budget (a stalled op holds
                // it). Never block shutdown: dispose the transport directly so the
                // child process tree is killed and the window can always close.
                _transport?.Dispose();
            }

            foreach (var item in _terminals.ToArray())
            {
                if (_terminals.TryRemove(item.Key, out var terminal))
                    StopAndDisposeTerminal(terminal);
            }
        }
        finally
        {
            if (lockTaken)
                _transportLock.Release();
        }

        _serviceLifetimeCts.Dispose();
    }
}
