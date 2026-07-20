using PSX.Models;

namespace PSX.Services;

public sealed class WorkspaceEventArgs : EventArgs
{
    public required WorkspaceDescriptor Workspace { get; init; }
}

public sealed class WorkspaceClosedEventArgs : EventArgs
{
    public Guid WorkspaceId { get; init; }
}

public interface IWorkspaceManager : IDisposable
{
    IReadOnlyList<WorkspaceDescriptor> Workspaces { get; }
    IReadOnlyList<AgentProviderCatalogItem> AgentProviders { get; }
    Task<Guid?> CreateTerminalAsync(ShellProfile? profile = null);
    Task<Guid?> CreateAgentAsync(string providerKey, string? workingDirectory = null);
    Task<Guid?> OpenAgentThreadAsync(string threadId);
    Task ActivateAsync(Guid workspaceId);
    Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason = WorkspaceCloseReason.User);
    Guid? FindOpenThread(string threadId);
    void BeginShutdown();

    event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    event EventHandler<WorkspaceEventArgs>? WorkspaceChanged;
    event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;
    event EventHandler<Guid>? WorkspaceActivationRequested;
}

public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly ITabManagementService _terminalTabs;
    private readonly IAgentWorkspaceCoordinator _agents;
    private readonly IAgentBridgeService _bridge;
    private readonly List<WorkspaceDescriptor> _workspaces = new();
    private readonly object _sync = new();
    private Guid? _activeWorkspaceId;
    private bool _creatingReplacementTerminal;
    private bool _shuttingDown;
    private bool _disposed;

    public WorkspaceManager(
        ITabManagementService terminalTabs,
        IAgentWorkspaceCoordinator agents,
        IAgentBridgeService bridge)
    {
        _terminalTabs = terminalTabs;
        _agents = agents;
        _bridge = bridge;

        _terminalTabs.TabCreated += OnTerminalCreated;
        _terminalTabs.TabClosed += OnTerminalClosed;
        _terminalTabs.TabTitleChanged += OnTerminalTitleChanged;
        _agents.WorkspaceCreated += OnAgentCreated;
        _agents.WorkspaceChanged += OnAgentChanged;
        _agents.WorkspaceClosed += OnAgentClosed;
        _agents.WorkspaceActivationRequested += OnActivationRequested;
    }

    public IReadOnlyList<WorkspaceDescriptor> Workspaces
    {
        get
        {
            lock (_sync)
                return _workspaces.ToArray();
        }
    }

    public IReadOnlyList<AgentProviderCatalogItem> AgentProviders => _agents.ProviderCatalog;

    public event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    public event EventHandler<WorkspaceEventArgs>? WorkspaceChanged;
    public event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;
    public event EventHandler<Guid>? WorkspaceActivationRequested;

    public async Task<Guid?> CreateTerminalAsync(ShellProfile? profile = null)
    {
        if (_disposed || _shuttingDown)
            return null;
        return await _terminalTabs.CreateTabAsync(profile).ConfigureAwait(false);
    }

    public Task<Guid?> CreateAgentAsync(string providerKey, string? workingDirectory = null) =>
        _disposed || _shuttingDown
            ? Task.FromResult<Guid?>(null)
            : _agents.CreateAsync(providerKey, workingDirectory);

    public Task<Guid?> OpenAgentThreadAsync(string threadId) =>
        _disposed || _shuttingDown
            ? Task.FromResult<Guid?>(null)
            : _agents.OpenThreadAsync(threadId);

    public async Task ActivateAsync(Guid workspaceId)
    {
        WorkspaceDescriptor? workspace;
        lock (_sync)
            workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
            return;

        lock (_sync)
            _activeWorkspaceId = workspaceId;

        if (workspace.Kind == WorkspaceKind.Terminal)
        {
            // The Agent coordinator owns the active Agent runtime status. A
            // Terminal activation must clear it so later runtime updates from
            // a hidden Agent cannot make the native status bar reappear.
            _agents.DeactivateRuntimeStatus();
            await _terminalTabs.SwitchTabAsync(workspaceId).ConfigureAwait(false);
            await _bridge.SendEventAsync(new
            {
                type = "workspace_activated",
                workspaceId,
                kind = "terminal"
            }).ConfigureAwait(false);
            WorkspaceActivationRequested?.Invoke(this, workspaceId);
        }
        else
        {
            await _agents.ActivateAsync(workspaceId).ConfigureAwait(false);
        }
    }

    public async Task CloseAsync(
        Guid workspaceId,
        WorkspaceCloseReason reason = WorkspaceCloseReason.User)
    {
        WorkspaceDescriptor? workspace;
        lock (_sync)
            workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == workspaceId);

        if (workspace == null)
            return;

        if (workspace.Kind == WorkspaceKind.Terminal)
            await _terminalTabs.CloseTabAsync(workspaceId).ConfigureAwait(false);
        else
            await _agents.CloseAsync(workspaceId, reason).ConfigureAwait(false);
    }

    public Guid? FindOpenThread(string threadId) => _agents.FindOpenThread(threadId);

    public void BeginShutdown() => _shuttingDown = true;

    private void OnTerminalCreated(object? sender, TabCreatedEventArgs args)
    {
        var workspace = new WorkspaceDescriptor
        {
            WorkspaceId = args.SessionId,
            Kind = WorkspaceKind.Terminal,
            IconKey = "terminal",
            Title = args.Title
        };
        lock (_sync)
        {
            _workspaces.Add(workspace);
            _creatingReplacementTerminal = false;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = workspace });
        _ = ActivateAsync(workspace.WorkspaceId);
    }

    private void OnTerminalClosed(object? sender, TabClosedEventArgs args) =>
        RemoveWorkspace(args.SessionId);

    private void OnTerminalTitleChanged(object? sender, TabTitleChangedEventArgs args)
    {
        WorkspaceDescriptor? workspace;
        lock (_sync)
        {
            workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == args.SessionId);
            if (workspace != null)
                workspace.Title = args.Title;
        }
        if (workspace != null)
            WorkspaceChanged?.Invoke(this, new WorkspaceEventArgs { Workspace = workspace });
    }

    private void OnAgentCreated(object? sender, AgentWorkspaceEventArgs args)
    {
        lock (_sync)
        {
            _workspaces.Add(args.Workspace);
            _creatingReplacementTerminal = false;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = args.Workspace });
    }

    private void OnAgentChanged(object? sender, AgentWorkspaceEventArgs args) =>
        WorkspaceChanged?.Invoke(this, new WorkspaceEventArgs { Workspace = args.Workspace });

    private void OnAgentClosed(object? sender, AgentWorkspaceClosedEventArgs args) =>
        RemoveWorkspace(args.WorkspaceId);

    private void OnActivationRequested(object? sender, Guid workspaceId)
    {
        lock (_sync)
            _activeWorkspaceId = workspaceId;
        WorkspaceActivationRequested?.Invoke(this, workspaceId);
    }

    private void RemoveWorkspace(Guid workspaceId)
    {
        var removed = false;
        Guid? nextWorkspaceId = null;
        var createReplacementTerminal = false;
        lock (_sync)
        {
            var workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == workspaceId);
            if (workspace != null)
            {
                var removedIndex = _workspaces.IndexOf(workspace);
                _workspaces.Remove(workspace);
                removed = true;

                if (_activeWorkspaceId == workspaceId)
                {
                    _activeWorkspaceId = null;
                    if (_workspaces.Count > 0)
                    {
                        var nextIndex = Math.Min(removedIndex, _workspaces.Count - 1);
                        nextWorkspaceId = _workspaces[nextIndex].WorkspaceId;
                    }
                }

                if (_workspaces.Count == 0 && !_shuttingDown && !_creatingReplacementTerminal)
                {
                    _creatingReplacementTerminal = true;
                    createReplacementTerminal = true;
                }
            }
        }
        if (removed)
            WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs { WorkspaceId = workspaceId });
        if (nextWorkspaceId.HasValue)
            _ = ActivateAsync(nextWorkspaceId.Value);
        else if (createReplacementTerminal)
            _ = CreateTerminalAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shuttingDown = true;

        _terminalTabs.TabCreated -= OnTerminalCreated;
        _terminalTabs.TabClosed -= OnTerminalClosed;
        _terminalTabs.TabTitleChanged -= OnTerminalTitleChanged;
        _agents.WorkspaceCreated -= OnAgentCreated;
        _agents.WorkspaceChanged -= OnAgentChanged;
        _agents.WorkspaceClosed -= OnAgentClosed;
        _agents.WorkspaceActivationRequested -= OnActivationRequested;
    }
}
