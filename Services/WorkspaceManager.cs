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
    /// <summary>Move a workspace into a fresh right-hand pane (split).</summary>
    void SplitWorkspaceToNewPane(Guid workspaceId);
    /// <summary>Focus a pane without changing assignments (click inside it).</summary>
    void FocusPane(string paneId);
    /// <summary>Collapse back to a single pane; other workspaces go background.</summary>
    void CollapseToSinglePane();
    /// <summary>Divider drag end: set one pane's width share.</summary>
    void SetPaneRatio(string paneId, double ratio);
    /// <summary>Creation transaction: the next created workspace lands here
    /// (<see cref="WorkspaceLayoutService.NewPanePlacement"/> for a fresh pane).</summary>
    void RecordPendingPlacement(string paneId);
    /// <summary>Current requested-layout snapshot (panes, focus, ratios).</summary>
    WorkspaceLayoutSnapshot LayoutSnapshot { get; }
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
    private readonly WorkspaceLayoutService _layout;
    private readonly List<WorkspaceDescriptor> _workspaces = new();
    private readonly object _sync = new();
    private Guid? _activeWorkspaceId;
    private bool _creatingReplacementTerminal;
    private bool _shuttingDown;
    private bool _disposed;

    public WorkspaceManager(
        ITabManagementService terminalTabs,
        IAgentWorkspaceCoordinator agents,
        IAgentBridgeService bridge,
        WorkspaceLayoutService layout)
    {
        _terminalTabs = terminalTabs;
        _agents = agents;
        _bridge = bridge;
        _layout = layout;
        _layout.LayoutChanged += OnLayoutChanged;

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

        // Set before Assign so the layout-changed handler sees the activation
        // as already applied and skips duplicate side effects.
        lock (_sync)
            _activeWorkspaceId = workspaceId;

        // The requested-layout truth source: activation assigns the workspace
        // into the focused pane (or focuses the pane already showing it).
        _layout.AssignActiveWorkspace(workspaceId, workspace.Kind);

        await ApplyActiveWorkspaceAsync(workspace).ConfigureAwait(false);
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

    public WorkspaceLayoutSnapshot LayoutSnapshot => _layout.Snapshot;

    public void SplitWorkspaceToNewPane(Guid workspaceId)
    {
        WorkspaceDescriptor? workspace;
        lock (_sync)
            workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == workspaceId);
        if (workspace == null)
            return;
        _layout.SplitWorkspaceToNewPane(workspaceId, workspace.Kind);
    }

    public void FocusPane(string paneId) => _layout.FocusPane(paneId);

    public void CollapseToSinglePane() => _layout.CollapseToSinglePane();

    public void SetPaneRatio(string paneId, double ratio) => _layout.SetPaneRatio(paneId, ratio);

    public void RecordPendingPlacement(string paneId) => _layout.RecordPendingPlacement(paneId);

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
        _layout.AssignActiveWorkspace(workspaceId, WorkspaceKind.Agent);
        WorkspaceActivationRequested?.Invoke(this, workspaceId);
    }

    private void RemoveWorkspace(Guid workspaceId)
    {
        var removed = false;
        var createReplacementTerminal = false;
        lock (_sync)
        {
            var workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == workspaceId);
            if (workspace != null)
            {
                _workspaces.Remove(workspace);
                removed = true;

                if (_activeWorkspaceId == workspaceId)
                    _activeWorkspaceId = null;

                if (_workspaces.Count == 0 && !_shuttingDown && !_creatingReplacementTerminal)
                {
                    _creatingReplacementTerminal = true;
                    createReplacementTerminal = true;
                }
            }
        }
        if (!removed)
            return;
        // The layout collapses the vacated pane or pulls the MRU background
        // workspace in atomically; its changed event activates the new focus.
        _layout.RemoveWorkspace(workspaceId);
        WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs { WorkspaceId = workspaceId });
        if (createReplacementTerminal)
            _ = CreateTerminalAsync();
    }

    private void OnLayoutChanged(object? sender, WorkspaceLayoutSnapshot snapshot)
    {
        _ = SendLayoutSnapshotAsync(snapshot);
        // UI-initiated intents (focus a pane, close, collapse) change the
        // focused workspace without going through ActivateAsync — sync the
        // single active workspace from the focused pane.
        var focused = _layout.FocusedWorkspaceId;
        Guid? current;
        lock (_sync)
            current = _activeWorkspaceId;
        if (focused.HasValue && focused.Value != current)
            _ = ActivateFocusedWorkspaceAsync(focused.Value);
    }

    private async Task ActivateFocusedWorkspaceAsync(Guid workspaceId)
    {
        WorkspaceDescriptor? workspace;
        lock (_sync)
        {
            workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == workspaceId);
            if (workspace == null)
                return;
            _activeWorkspaceId = workspaceId;
        }
        await ApplyActiveWorkspaceAsync(workspace).ConfigureAwait(false);
    }

    private async Task ApplyActiveWorkspaceAsync(WorkspaceDescriptor workspace)
    {
        if (workspace.Kind == WorkspaceKind.Terminal)
        {
            await _terminalTabs.SwitchTabAsync(workspace.WorkspaceId).ConfigureAwait(false);
            await _bridge.SendEventAsync(new
            {
                type = "workspace_activated",
                workspaceId = workspace.WorkspaceId,
                kind = "terminal"
            }).ConfigureAwait(false);
            WorkspaceActivationRequested?.Invoke(this, workspace.WorkspaceId);
        }
        else
        {
            await _agents.ActivateAsync(workspace.WorkspaceId).ConfigureAwait(false);
        }
    }

    private Task SendLayoutSnapshotAsync(WorkspaceLayoutSnapshot snapshot) =>
        _bridge.SendEventAsync(new
        {
            type = "workspace_layout",
            revision = snapshot.LayoutRevision,
            focusedPaneId = snapshot.FocusedPaneId,
            panes = snapshot.Panes.Select(p => new
            {
                paneId = p.PaneId,
                workspaceId = p.WorkspaceId,
                kind = p.Kind?.ToString().ToLowerInvariant(),
                ratio = p.Ratio
            }).ToArray()
        });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shuttingDown = true;

        _layout.LayoutChanged -= OnLayoutChanged;
        _terminalTabs.TabCreated -= OnTerminalCreated;
        _terminalTabs.TabClosed -= OnTerminalClosed;
        _terminalTabs.TabTitleChanged -= OnTerminalTitleChanged;
        _agents.WorkspaceCreated -= OnAgentCreated;
        _agents.WorkspaceChanged -= OnAgentChanged;
        _agents.WorkspaceClosed -= OnAgentClosed;
        _agents.WorkspaceActivationRequested -= OnActivationRequested;
    }
}
