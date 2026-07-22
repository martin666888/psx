using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using PSX.Models;

namespace PSX.Services;

public sealed class AgentWorkspaceEventArgs : EventArgs
{
    public required WorkspaceDescriptor Workspace { get; init; }
}

public sealed class AgentWorkspaceClosedEventArgs : EventArgs
{
    public Guid WorkspaceId { get; init; }
}

/// <summary>
/// The runtime status that belongs to the currently active Agent Workspace.
/// A null WorkspaceId and Message clear the native status bar, for example
/// when the user switches to a Terminal Workspace.
/// </summary>
public sealed class ActiveRuntimeStatusChangedEventArgs : EventArgs
{
    public Guid? WorkspaceId { get; init; }
    public string? ProviderKey { get; init; }
    public string? ProviderDisplayName { get; init; }
    public string? Message { get; init; }
}

public interface IAgentWorkspaceCoordinator : IDisposable
{
    IReadOnlyList<WorkspaceDescriptor> Workspaces { get; }
    IReadOnlyList<AgentProviderCatalogItem> ProviderCatalog { get; }
    Task<Guid?> CreateAsync(string providerKey, string? workingDirectory = null);
    Task<Guid?> OpenThreadAsync(string threadId);
    Task ActivateAsync(Guid workspaceId);
    void DeactivateRuntimeStatus();
    Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason);
    Task ShutdownAsync();
    Guid? FindOpenThread(string threadId);
    Task PublishStateAsync();

    event EventHandler<AgentWorkspaceEventArgs>? WorkspaceCreated;
    event EventHandler<AgentWorkspaceEventArgs>? WorkspaceChanged;
    event EventHandler<AgentWorkspaceClosedEventArgs>? WorkspaceClosed;
    event EventHandler<Guid>? WorkspaceActivationRequested;
    event EventHandler<ActiveRuntimeStatusChangedEventArgs>? ActiveRuntimeStatusChanged;
}

public sealed class AgentWorkspaceCoordinator : IAgentWorkspaceCoordinator
{
    public const int MaxAgentWorkspaces = 5;

    private sealed class Entry
    {
        public required WorkspaceDescriptor Descriptor { get; init; }
        public required AgentWorkspaceSessionHandle Handle { get; init; }
        public IAgentWorkspaceSession Session => Handle.Session;
        public AgentWorkspaceEventSink EventSink => Handle.EventSink;
        public bool WaitingForPermission { get; set; }
        public bool WaitingForInput { get; set; }
        public bool Closing { get; set; }
    }

    private sealed class RuntimeStatusSubscription
    {
        public required IAcpAgentRuntime Runtime { get; init; }
        public required Action<string> Handler { get; init; }
    }

    private readonly IAgentBridgeService _rootBridge;
    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentProviderRegistry _providerRegistry;
    private readonly IAgentWorkspaceFactory _workspaceFactory;
    private readonly IAgentHistoryCatalog _historyCatalog;
    private readonly SemaphoreSlim _creationLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, Guid> _openThreads = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _shutdownLock = new();
    private readonly object _runtimeStatusLock = new();
    private readonly List<RuntimeStatusSubscription> _runtimeStatusSubscriptions = [];
    private readonly Dictionary<IAcpAgentRuntime, string?> _runtimeStatusMessages =
        new(ReferenceEqualityComparer.Instance);
    private Guid? _activeAgentWorkspaceId;
    private Task? _shutdownTask;
    private bool _disposed;

    public AgentWorkspaceCoordinator(
        IAgentBridgeService rootBridge,
        IAgentThreadStore threadStore,
        IAgentProviderRegistry providerRegistry,
        IAgentWorkspaceFactory workspaceFactory,
        IAgentHistoryCatalog historyCatalog)
    {
        _rootBridge = rootBridge;
        _threadStore = threadStore;
        _providerRegistry = providerRegistry;
        _workspaceFactory = workspaceFactory;
        _historyCatalog = historyCatalog;

        _threadStore.DeleteEmptyDrafts();

        _rootBridge.UserMessageSubmitted += OnSubmit;
        _rootBridge.CommandReceived += OnCommand;
        _rootBridge.AttachmentUploadReceived += OnUpload;
        _historyCatalog.Invalidated += OnHistoryInvalidated;
        SubscribeRuntimeStatusEvents();
    }

    public IReadOnlyList<WorkspaceDescriptor> Workspaces =>
        _entries.Values.Select(entry => entry.Descriptor).ToArray();

    public IReadOnlyList<AgentProviderCatalogItem> ProviderCatalog =>
        _providerRegistry.Providers.Select(provider => new AgentProviderCatalogItem(
            provider.Descriptor.Key,
            provider.Descriptor.DisplayName,
            provider.Descriptor.AssistantName,
            ReferenceEquals(provider, _providerRegistry.DefaultProvider))).ToArray();

    public event EventHandler<AgentWorkspaceEventArgs>? WorkspaceCreated;
    public event EventHandler<AgentWorkspaceEventArgs>? WorkspaceChanged;
    public event EventHandler<AgentWorkspaceClosedEventArgs>? WorkspaceClosed;
    public event EventHandler<Guid>? WorkspaceActivationRequested;
    public event EventHandler<ActiveRuntimeStatusChangedEventArgs>? ActiveRuntimeStatusChanged;

    public async Task<Guid?> CreateAsync(string providerKey, string? workingDirectory = null)
    {
        if (_disposed)
            return null;

        await _creationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_entries.Count >= MaxAgentWorkspaces)
            {
                await SendLimitReachedAsync().ConfigureAwait(false);
                return null;
            }

            var provider = _providerRegistry.Find(providerKey);
            if (provider == null)
                return null;

            var cwd = ResolveDraftWorkingDirectory(workingDirectory);
            var thread = _threadStore.CreateThread(cwd);
            thread.Provider = provider.Descriptor.Key;
            _threadStore.SaveThread(thread);
            return await CreateEntryAsync(thread, provider, restore: false).ConfigureAwait(false);
        }
        finally
        {
            _creationLock.Release();
        }
    }

    public async Task<Guid?> OpenThreadAsync(string threadId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(threadId))
            return null;

        await _creationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_openThreads.TryGetValue(threadId, out var existing))
            {
                await ActivateAsync(existing).ConfigureAwait(false);
                return existing;
            }

            if (_entries.Count >= MaxAgentWorkspaces)
            {
                await SendLimitReachedAsync().ConfigureAwait(false);
                return null;
            }

            var thread = _threadStore.LoadThread(threadId);
            if (thread == null)
                return null;

            var provider = _providerRegistry.Find(thread.Provider);
            return await CreateEntryAsync(
                thread,
                provider,
                restore: true).ConfigureAwait(false);
        }
        finally
        {
            _creationLock.Release();
        }
    }

    private async Task<Guid?> CreateEntryAsync(
        AgentThread thread,
        IAcpAgentProvider? provider,
        bool restore)
    {
        var workspaceId = Guid.NewGuid();

        var descriptor = new WorkspaceDescriptor
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceKind.Agent,
            IconKey = "agent",
            Title = thread.Messages.Count == 0
                ? provider?.Descriptor.DisplayName ?? thread.Provider
                : thread.Title,
            ProviderKey = thread.Provider,
            ProviderName = _providerRegistry.Find(thread.Provider)?.Descriptor.DisplayName ?? thread.Provider,
            ThreadId = thread.ThreadId,
            WorkingDirectory = thread.Cwd,
            AgentState = thread.Messages.Count == 0
                ? AgentWorkspaceState.Draft
                : _providerRegistry.Find(thread.Provider) == null
                    ? AgentWorkspaceState.TranscriptOnly
                    : AgentWorkspaceState.Idle
        };

        var handle = _workspaceFactory.Create(
            workspaceId,
            provider,
            thread,
            message => BeforeWorkspaceEvent(workspaceId, message));

        var entry = new Entry
        {
            Descriptor = descriptor,
            Handle = handle
        };

        if (!_entries.TryAdd(workspaceId, entry))
        {
            handle.Dispose();
            return null;
        }
        if (!_openThreads.TryAdd(thread.ThreadId, workspaceId))
        {
            _entries.TryRemove(workspaceId, out _);
            handle.Dispose();
            return null;
        }

        WorkspaceCreated?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = descriptor });
        await _rootBridge.SendEventAsync(new
        {
            type = "agent_workspace_created",
            workspaceId,
            providerKey = descriptor.ProviderKey,
            providerName = descriptor.ProviderName,
            threadId = descriptor.ThreadId
        }).ConfigureAwait(false);

        // Re-publish the provider catalog with every workspace: the startup
        // broadcast can land before the WebView page subscribes, and the
        // global History dock needs the catalog whenever a workspace exists.
        await PublishProvidersAsync().ConfigureAwait(false);

        if (restore)
            await entry.Session.RestoreAsync().ConfigureAwait(false);
        else
            await entry.Session.PublishStateAsync().ConfigureAwait(false);

        await ActivateAsync(workspaceId).ConfigureAwait(false);
        return workspaceId;
    }

    public Task ActivateAsync(Guid workspaceId)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry) || entry.Closing)
            return Task.CompletedTask;

        lock (_runtimeStatusLock)
            _activeAgentWorkspaceId = workspaceId;

        PublishActiveRuntimeStatus(entry);
        WorkspaceActivationRequested?.Invoke(this, workspaceId);
        return _rootBridge.SendEventAsync(new
        {
            type = "workspace_activated",
            workspaceId,
            kind = "agent"
        });
    }

    public void DeactivateRuntimeStatus()
    {
        lock (_runtimeStatusLock)
            _activeAgentWorkspaceId = null;

        ActiveRuntimeStatusChanged?.Invoke(this, new ActiveRuntimeStatusChangedEventArgs());
    }

    public async Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry) || entry.Closing)
            return;

        var wasActive = false;
        lock (_runtimeStatusLock)
        {
            if (_activeAgentWorkspaceId == workspaceId)
            {
                _activeAgentWorkspaceId = null;
                wasActive = true;
            }
        }
        if (wasActive)
            ActiveRuntimeStatusChanged?.Invoke(this, new ActiveRuntimeStatusChangedEventArgs());

        entry.Closing = true;
        _entries.TryRemove(workspaceId, out _);
        _openThreads.TryRemove(entry.Descriptor.ThreadId ?? "", out _);
        WorkspaceClosed?.Invoke(this, new AgentWorkspaceClosedEventArgs { WorkspaceId = workspaceId });

        await _rootBridge.SendEventAsync(new
        {
            type = "agent_workspace_closed",
            workspaceId
        }).ConfigureAwait(false);

        try
        {
            await entry.Session.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        if (entry.Session.IsDraft && !string.IsNullOrWhiteSpace(entry.Descriptor.ThreadId))
            _threadStore.DeleteThread(entry.Descriptor.ThreadId);

        _historyCatalog.Invalidate();

        entry.Handle.Dispose();
    }

    public Guid? FindOpenThread(string threadId) =>
        _openThreads.TryGetValue(threadId, out var workspaceId) ? workspaceId : null;

    public async Task PublishStateAsync()
    {
        await PublishProvidersAsync().ConfigureAwait(false);

        foreach (var entry in _entries.Values)
            await entry.Session.PublishStateAsync().ConfigureAwait(false);
    }

    private Task PublishProvidersAsync()
    {
        return _rootBridge.SendEventAsync(new
        {
            type = "agent_providers",
            providers = ProviderCatalog.Select(provider => new
            {
                key = provider.Key,
                displayName = provider.DisplayName,
                assistantName = provider.AssistantName,
                isDefault = provider.IsDefault
            }).ToArray()
        });
    }

    private bool BeforeWorkspaceEvent(Guid workspaceId, JsonObject message)
    {
        if (!_entries.TryGetValue(workspaceId, out var entry) || entry.Closing)
            return false;

        var type = message["type"]?.GetValue<string>() ?? "";
        if (type == "agent_workspace_close_requested")
        {
            _ = CloseAsync(workspaceId, WorkspaceCloseReason.ThreadDeleted);
            return false;
        }

        if (type == "agent_state")
        {
            entry.Descriptor.Title = message["title"]?.GetValue<string>() ?? entry.Descriptor.Title;
            entry.Descriptor.WorkingDirectory = message["cwd"]?.GetValue<string>() ?? entry.Descriptor.WorkingDirectory;
            entry.Descriptor.AgentState = ResolveState(message, entry);
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (type == "user_message")
        {
            entry.Descriptor.Title = message["title"]?.GetValue<string>() ?? entry.Descriptor.Title;
            entry.Descriptor.AgentState = AgentWorkspaceState.Running;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (type == "permission_request")
        {
            entry.WaitingForPermission = true;
            entry.Descriptor.AgentState = AgentWorkspaceState.WaitingForPermission;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (type is "question_request" or "elicitation_request")
        {
            entry.WaitingForInput = true;
            entry.Descriptor.AgentState = AgentWorkspaceState.WaitingForInput;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (type is "permission_resolved" or "permission_cancelled" or "elicitation_cancelled")
        {
            entry.WaitingForPermission = false;
            entry.WaitingForInput = false;
            entry.Descriptor.AgentState = AgentWorkspaceState.Running;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (type == "run_finished")
        {
            entry.WaitingForPermission = false;
            entry.WaitingForInput = false;
            entry.Descriptor.AgentState = AgentWorkspaceState.Idle;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (type == "run_failed")
        {
            entry.WaitingForPermission = false;
            entry.WaitingForInput = false;
            entry.Descriptor.AgentState = AgentWorkspaceState.Error;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }

        if (type is "user_message" or "run_finished" or "run_failed" or "agent_cleared")
            _historyCatalog.Invalidate();

        return true;
    }

    private static AgentWorkspaceState ResolveState(JsonObject message, Entry entry)
    {
        var status = message["status"]?.GetValue<string>() ?? "";
        var busy = message["busy"]?.GetValue<bool>() ?? false;
        if (string.Equals(status, "transcript_only", StringComparison.OrdinalIgnoreCase))
            return AgentWorkspaceState.TranscriptOnly;
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return AgentWorkspaceState.Error;
        if (entry.WaitingForPermission)
            return AgentWorkspaceState.WaitingForPermission;
        if (entry.WaitingForInput)
            return AgentWorkspaceState.WaitingForInput;
        if (busy)
            return AgentWorkspaceState.Running;
        return entry.Session.IsDraft ? AgentWorkspaceState.Draft : AgentWorkspaceState.Idle;
    }

    private string ResolveDraftWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
            return Path.GetFullPath(requested);

        try
        {
            var recent = _threadStore.ListThreads().FirstOrDefault()?.Cwd;
            if (!string.IsNullOrWhiteSpace(recent) && Directory.Exists(recent))
                return recent!;
        }
        catch
        {
        }

        return Environment.CurrentDirectory;
    }

    private Task SendLimitReachedAsync() => _rootBridge.SendEventAsync(new
    {
        type = "agent_workspace_limit_reached",
        limit = MaxAgentWorkspaces,
        text = $"You can open up to {MaxAgentWorkspaces} Agent tabs at the same time."
    });

    private void OnSubmit(object? sender, AgentSubmitEventArgs args)
    {
        if (_entries.TryGetValue(args.WorkspaceId, out var entry) && !entry.Closing)
            entry.EventSink.Submit(args);
    }

    private void OnUpload(object? sender, AgentAttachmentUploadEventArgs args)
    {
        if (_entries.TryGetValue(args.WorkspaceId, out var entry) && !entry.Closing)
            entry.EventSink.Upload(args);
    }

    private void OnCommand(object? sender, AgentCommandEventArgs args)
    {
        if (!_entries.TryGetValue(args.WorkspaceId, out var entry) || entry.Closing)
            return;

        if (args.Command == "load_thread" && !string.IsNullOrWhiteSpace(args.Value))
        {
            _ = OpenThreadAsync(args.Value);
            return;
        }

        if (args.Command == "agent_permission_response")
        {
            entry.WaitingForPermission = false;
            entry.Descriptor.AgentState = AgentWorkspaceState.Running;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }
        else if (args.Command is "agent_question_response" or "agent_elicitation_response")
        {
            entry.WaitingForInput = false;
            entry.Descriptor.AgentState = AgentWorkspaceState.Running;
            WorkspaceChanged?.Invoke(this, new AgentWorkspaceEventArgs { Workspace = entry.Descriptor });
        }

        entry.EventSink.Command(args);
    }

    private void OnHistoryInvalidated(object? sender, EventArgs args)
    {
        foreach (var entry in _entries.Values)
            _ = entry.EventSink.SendEventAsync(new { type = "agent_history_invalidated" });
    }

    private void SubscribeRuntimeStatusEvents()
    {
        var subscribedRuntimes = new HashSet<IAcpAgentRuntime>(ReferenceEqualityComparer.Instance);
        foreach (var provider in _providerRegistry.Providers)
        {
            var runtime = provider.Runtime;
            if (!subscribedRuntimes.Add(runtime))
                continue;

            Action<string> handler = message => OnRuntimeStatusChanged(runtime, message);
            runtime.StatusChanged += handler;
            _runtimeStatusSubscriptions.Add(new RuntimeStatusSubscription
            {
                Runtime = runtime,
                Handler = handler
            });
        }
    }

    private void OnRuntimeStatusChanged(IAcpAgentRuntime runtime, string message)
    {
        Entry? activeEntry = null;
        lock (_runtimeStatusLock)
        {
            _runtimeStatusMessages[runtime] = message;
            if (_activeAgentWorkspaceId is { } activeWorkspaceId
                && _entries.TryGetValue(activeWorkspaceId, out var entry)
                && !entry.Closing
                && UsesRuntime(entry, runtime))
            {
                activeEntry = entry;
            }
        }

        if (activeEntry != null)
            PublishActiveRuntimeStatus(activeEntry);
    }

    private void PublishActiveRuntimeStatus(Entry entry)
    {
        var provider = _providerRegistry.Find(entry.Descriptor.ProviderKey);
        if (provider == null)
        {
            ActiveRuntimeStatusChanged?.Invoke(this, new ActiveRuntimeStatusChangedEventArgs());
            return;
        }

        string? message;
        lock (_runtimeStatusLock)
        {
            if (!_runtimeStatusMessages.TryGetValue(provider.Runtime, out message))
                message = TryBuildRuntimeStatusText(provider.Runtime);
        }

        ActiveRuntimeStatusChanged?.Invoke(this, new ActiveRuntimeStatusChangedEventArgs
        {
            WorkspaceId = entry.Descriptor.WorkspaceId,
            ProviderKey = entry.Descriptor.ProviderKey,
            ProviderDisplayName = entry.Descriptor.ProviderName ?? provider.Descriptor.DisplayName,
            Message = message
        });
    }

    private bool UsesRuntime(Entry entry, IAcpAgentRuntime runtime)
    {
        var provider = _providerRegistry.Find(entry.Descriptor.ProviderKey);
        return provider != null && ReferenceEquals(provider.Runtime, runtime);
    }

    private static string? TryBuildRuntimeStatusText(IAcpAgentRuntime runtime)
    {
        try
        {
            return runtime.BuildStatusText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Unable to read Agent runtime status: " + ex);
            return null;
        }
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownLock)
        {
            if (_shutdownTask != null)
                return _shutdownTask;

            _disposed = true;
            _rootBridge.UserMessageSubmitted -= OnSubmit;
            _rootBridge.CommandReceived -= OnCommand;
            _rootBridge.AttachmentUploadReceived -= OnUpload;
            _historyCatalog.Invalidated -= OnHistoryInvalidated;
            foreach (var subscription in _runtimeStatusSubscriptions)
                subscription.Runtime.StatusChanged -= subscription.Handler;
            _runtimeStatusSubscriptions.Clear();
            DeactivateRuntimeStatus();
            _shutdownTask = ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var closeTasks = _entries.Keys
            .Select(CloseForShutdownAsync)
            .ToArray();
        await Task.WhenAll(closeTasks).ConfigureAwait(false);
    }

    private async Task CloseForShutdownAsync(Guid workspaceId)
    {
        try
        {
            await CloseAsync(workspaceId, WorkspaceCloseReason.ApplicationShutdown).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to close Agent Workspace {workspaceId} during shutdown: {ex}");
        }
    }

    public void Dispose()
    {
        // MainWindow awaits ShutdownAsync before disposing services. This
        // synchronous fallback is for non-UI owners and completed shutdowns.
        ShutdownAsync().GetAwaiter().GetResult();
    }
}
