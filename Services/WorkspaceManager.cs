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
    /// <summary>Create (or focus) the single DeepSeek Harness web workspace.</summary>
    Task<Guid?> CreateDshWebAsync();
    Task<Guid?> OpenAgentThreadAsync(string threadId);
    Task ActivateAsync(Guid workspaceId);
    Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason = WorkspaceCloseReason.User);
    Guid? FindOpenThread(string threadId);
    /// <summary>Move a workspace's tab into a fresh right-hand column (split).</summary>
    void SplitWorkspaceToNewPane(Guid workspaceId);
    /// <summary>Focus a column without changing assignments (click inside it).
    /// The id is a column id; the <c>pane_focus</c> wire name is preserved.</summary>
    void FocusPane(string columnId);
    /// <summary>Move the focus by one column (keyboard shortcut path).</summary>
    void FocusAdjacentPane(int delta);
    /// <summary>Merge every column's tabs into the focused column (single column).</summary>
    void CollapseToSinglePane();
    /// <summary>Divider drag end: set one column's width share.</summary>
    bool SetPaneRatios(long baseRevision, IReadOnlyDictionary<string, double> ratios);
    /// <summary>Toggle presentation-only zoom on the focused column.</summary>
    void TogglePaneZoom();
    /// <summary>Creation transaction: the next created workspace lands here
    /// (<see cref="WorkspaceLayoutService.NewPanePlacement"/> for a fresh column).</summary>
    void RecordPendingPlacement(string columnId);
    /// <summary>Tab badge source: needs attention and not focused-visible.</summary>
    bool IsAttentionNeeded(Guid workspaceId);
    /// <summary>Current requested-layout snapshot (columns, focus, ratios).</summary>
    WorkspaceLayoutSnapshot LayoutSnapshot { get; }
    /// <summary>Requested layout changed (column assignment, focus, ratios).</summary>
    event EventHandler<WorkspaceLayoutSnapshot>? LayoutChanged;
    void BeginShutdown();

    event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    event EventHandler<WorkspaceEventArgs>? WorkspaceChanged;
    event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;
    event EventHandler<Guid>? WorkspaceActivationRequested;
    event EventHandler<Guid>? AttentionNotificationRequested;
}

public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly ITabManagementService _terminalTabs;
    private readonly IAgentWorkspaceCoordinator _agents;
    private readonly IDshWebWorkspaceCoordinator _dsh;
    private readonly IAgentBridgeService _bridge;
    private readonly WorkspaceLayoutService _layout;
    private readonly List<WorkspaceDescriptor> _workspaces = new();
    private readonly object _sync = new();
    private readonly Dictionary<Guid, string> _attention = new();
    private readonly Dictionary<Guid, AgentWorkspaceState?> _lastStates = new();
    private readonly HashSet<string> _sharedWorktreeConflicts = new(StringComparer.OrdinalIgnoreCase);
    private long _catalogRevision;
    private Guid? _activeWorkspaceId;
    private bool _creatingReplacementTerminal;
    private bool _shuttingDown;
    private bool _disposed;

    public WorkspaceManager(
        ITabManagementService terminalTabs,
        IAgentWorkspaceCoordinator agents,
        IAgentBridgeService bridge,
        WorkspaceLayoutService layout,
        IDshWebWorkspaceCoordinator? dsh = null)
    {
        _terminalTabs = terminalTabs;
        _agents = agents;
        _bridge = bridge;
        _layout = layout;
        _dsh = dsh ?? new DshWebWorkspaceCoordinator(
            new DshWebRuntimeSupervisor(
                new DshWebRuntime(new RuntimeLocator(), System.IO.Path.GetTempPath()),
                bridge,
                System.IO.Path.GetTempPath()));
        _layout.LayoutChanged += OnLayoutChanged;

        _terminalTabs.TabCreated += OnTerminalCreated;
        _terminalTabs.TabClosed += OnTerminalClosed;
        _terminalTabs.TabTitleChanged += OnTerminalTitleChanged;
        _terminalTabs.PaneFocusRequested += (_, columnId) => _layout.FocusPane(columnId);
        _terminalTabs.PaneRatiosRequested += OnPaneRatiosRequested;
        _terminalTabs.PaneMoveRequested += OnPaneMoveRequested;
        _terminalTabs.WorkspaceLayoutIntentRequested += OnWorkspaceLayoutIntentRequested;
        _terminalTabs.WorkspaceCreateRequested += OnWorkspaceCreateRequested;
        _terminalTabs.DshCommandRequested += OnDshCommandRequested;
        _agents.WorkspaceCreated += OnAgentCreated;
        _agents.WorkspaceChanged += OnAgentChanged;
        _agents.WorkspaceClosed += OnAgentClosed;
        _agents.WorkspaceActivationRequested += OnActivationRequested;
        _dsh.WorkspaceCreated += OnDshCreated;
        _dsh.WorkspaceClosed += OnDshClosed;
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
    public event EventHandler<Guid>? AttentionNotificationRequested;
    public event EventHandler<WorkspaceLayoutSnapshot>? LayoutChanged;

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

    public async Task<Guid?> CreateDshWebAsync()
    {
        if (_disposed || _shuttingDown)
            return null;
        // Single-instance: an already-open DSH workspace is activated in
        // place (pure jump), never duplicated.
        var existing = _dsh.OpenWorkspaceId;
        if (existing.HasValue)
        {
            await ActivateAsync(existing.Value).ConfigureAwait(false);
            return existing.Value;
        }
        var created = await _dsh.CreateAsync().ConfigureAwait(false);
        if (created.HasValue)
            await ActivateAsync(created.Value).ConfigureAwait(false);
        return created;
    }

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
        // as a tab (focused column by default) or, when already open, focuses
        // its column and activates the tab in place.
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
        else if (workspace.Kind == WorkspaceKind.DshWeb)
            await _dsh.CloseAsync(workspaceId, reason).ConfigureAwait(false);
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
        if (!_layout.SplitWorkspaceToNewPane(workspaceId))
            BroadcastCatalog();
    }

    public void FocusPane(string columnId) => _layout.FocusPane(columnId);

    public void FocusAdjacentPane(int delta) => _layout.FocusAdjacentPane(delta);

    public void CollapseToSinglePane() => _layout.CollapseToSinglePane();

    public bool SetPaneRatios(long baseRevision, IReadOnlyDictionary<string, double> ratios) =>
        _layout.SetPaneRatios(baseRevision, ratios);

    public void RecordPendingPlacement(string columnId) => _layout.RecordPendingPlacement(columnId);

    public void TogglePaneZoom() =>
        _ = _bridge.SendEventAsync(new { type = "pane_zoom_toggle" });

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
        BroadcastCatalog();
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
        {
            WorkspaceChanged?.Invoke(this, new WorkspaceEventArgs { Workspace = workspace });
            BroadcastCatalog();
        }
    }

    private void OnAgentCreated(object? sender, AgentWorkspaceEventArgs args)
    {
        lock (_sync)
        {
            _workspaces.Add(args.Workspace);
            _creatingReplacementTerminal = false;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = args.Workspace });
        BroadcastCatalog();
    }

    private void OnAgentChanged(object? sender, AgentWorkspaceEventArgs args)
    {
        WorkspaceDescriptor workspace = args.Workspace;
        lock (_sync)
        {
            var existing = _workspaces.FirstOrDefault(item => item.WorkspaceId == args.Workspace.WorkspaceId);
            if (existing != null)
            {
                existing.Title = args.Workspace.Title;
                existing.AgentState = args.Workspace.AgentState;
                existing.ProviderName = args.Workspace.ProviderName;
                existing.WorkingDirectory = args.Workspace.WorkingDirectory;
                workspace = existing;
            }
        }
        UpdateAttention(workspace);
        WorkspaceChanged?.Invoke(this, new WorkspaceEventArgs { Workspace = workspace });
        BroadcastCatalog();
    }

    /// <summary>Attention tracking: a workspace needs the user when it waits
    /// for permission/input, errors, or finishes a run - but only while its
    /// column is unfocused. Focusing the column acknowledges; returning to
    /// Running clears (the request was answered).</summary>
    private void UpdateAttention(WorkspaceDescriptor workspace)
    {
        if (workspace.Kind != WorkspaceKind.Agent)
            return;

        bool changed;
        string? notificationKind;
        lock (_sync)
        {
            var previous = _lastStates.TryGetValue(workspace.WorkspaceId, out var prev) ? prev : null;
            var current = workspace.AgentState;
            _lastStates[workspace.WorkspaceId] = current;

            var focused = _layout.FocusedWorkspaceId == workspace.WorkspaceId;
            string? attentionKind = current switch
            {
                AgentWorkspaceState.WaitingForPermission when !focused => "permission",
                AgentWorkspaceState.WaitingForInput when !focused => "question",
                AgentWorkspaceState.Error when !focused => "error",
                AgentWorkspaceState.Idle when !focused && previous == AgentWorkspaceState.Running => "completed",
                _ => null
            };
            notificationKind = attentionKind;

            if (attentionKind == null || focused || current == AgentWorkspaceState.Running)
                changed = _attention.Remove(workspace.WorkspaceId);
            else
            {
                changed = !_attention.TryGetValue(workspace.WorkspaceId, out var existing)
                    || !string.Equals(existing, attentionKind, StringComparison.Ordinal);
                _attention[workspace.WorkspaceId] = attentionKind;
            }
        }

        if (changed)
            BroadcastCatalog();
        if (changed && notificationKind is "permission" or "question" or "error")
            AttentionNotificationRequested?.Invoke(this, workspace.WorkspaceId);
    }

    /// <summary>Attention lives in the layout payload, so a state-only change
    /// rebroadcasts the current snapshot.</summary>
    private void BroadcastLayout()
    {
        var snapshot = _layout.Snapshot;
        _ = SendLayoutSnapshotAsync(snapshot);
        LayoutChanged?.Invoke(this, snapshot);
        BroadcastCatalog();
    }

    /// <summary>Tab badge source: needs attention and not focused-visible.</summary>
    public bool IsAttentionNeeded(Guid workspaceId)
    {
        lock (_sync)
            return _attention.ContainsKey(workspaceId);
    }

    private void OnAgentClosed(object? sender, AgentWorkspaceClosedEventArgs args) =>
        RemoveWorkspace(args.WorkspaceId);

    private void OnDshCreated(object? sender, WorkspaceEventArgs args)
    {
        lock (_sync)
        {
            _workspaces.Add(args.Workspace);
            _creatingReplacementTerminal = false;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = args.Workspace });
        BroadcastCatalog();
    }

    private void OnDshClosed(object? sender, WorkspaceClosedEventArgs args) =>
        RemoveWorkspace(args.WorkspaceId);

    private void OnDshCommandRequested(object? sender, DshCommandEventArgs args) =>
        _ = _dsh.HandleCommandAsync(args.Name);

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
        lock (_sync)
        {
            _attention.Remove(workspaceId);
            _lastStates.Remove(workspaceId);
        }
        // The layout removes the tab; closing the unique tab of the only
        // column leaves a fresh empty column for the replacement terminal.
        _layout.RemoveWorkspace(workspaceId);
        WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs { WorkspaceId = workspaceId });
        BroadcastCatalog();
        if (createReplacementTerminal)
            _ = CreateTerminalAsync();
    }

    private void OnPaneMoveRequested(object? sender, PaneMoveEventArgs args)
    {
        WorkspaceDescriptor? workspace;
        lock (_sync)
            workspace = _workspaces.FirstOrDefault(item => item.WorkspaceId == args.WorkspaceId);
        if (workspace == null)
            return;
        _layout.MoveWorkspaceToColumn(args.WorkspaceId, args.PaneId);
    }

    private void OnPaneRatiosRequested(object? sender, PaneRatiosEventArgs args)
    {
        if (_layout.SetPaneRatios(args.BaseRevision, args.Ratios))
            return;
        // A rejected vector still raises the revision: the follow-up
        // broadcast is otherwise dropped by the WebView's revision guard and
        // the drag preview would stick forever.
        _layout.TouchRevision();
        BroadcastLayout();
    }

    private void OnWorkspaceLayoutIntentRequested(object? sender, WorkspaceLayoutIntentEventArgs args)
    {
        switch (args.Action)
        {
            case "activate" when args.WorkspaceId.HasValue:
                _ = ActivateAsync(args.WorkspaceId.Value);
                break;
            case "close" when args.WorkspaceId.HasValue:
                _ = CloseAsync(args.WorkspaceId.Value);
                break;
            case "split_right" when args.WorkspaceId.HasValue:
                SplitWorkspaceToNewPane(args.WorkspaceId.Value);
                break;
            case "collapse_single":
                CollapseToSinglePane();
                break;
        }
    }

    private void OnWorkspaceCreateRequested(object? sender, WorkspaceCreateEventArgs args)
    {
        _ = CreateWorkspaceFromIntentAsync(args);
    }

    private async Task CreateWorkspaceFromIntentAsync(WorkspaceCreateEventArgs args)
    {
        if (args.Kind == "agent"
            && (string.IsNullOrWhiteSpace(args.ProviderKey)
                || !AgentProviders.Any(provider => string.Equals(provider.Key, args.ProviderKey, StringComparison.Ordinal))))
            return;
        if (args.Kind == "agent" && Workspaces.Count(workspace => workspace.Kind == WorkspaceKind.Agent) >= AgentWorkspaceCoordinator.MaxAgentWorkspaces)
        {
            await SendNoticeAsync($"最多支持 {AgentWorkspaceCoordinator.MaxAgentWorkspaces} 个 Agent 工作区").ConfigureAwait(false);
            return;
        }

        // new_right at the column cap degrades into a tab in the focused
        // column (no notice): the record is simply skipped, so the default
        // placement applies.
        if (args.Placement == "new_right" && _layout.Snapshot.Columns.Count < WorkspaceLayoutService.MaxColumns)
            _layout.RecordPendingPlacement(WorkspaceLayoutService.NewPanePlacement);

        var created = args.Kind switch
        {
            "terminal" => await CreateTerminalAsync().ConfigureAwait(false),
            "dsh_web" => await CreateDshWebAsync().ConfigureAwait(false),
            _ => await CreateAgentAsync(args.ProviderKey!).ConfigureAwait(false)
        };
        if (!created.HasValue && args.Placement == "new_right")
            _layout.CancelPendingPlacement();
    }

    private void OnLayoutChanged(object? sender, WorkspaceLayoutSnapshot snapshot)
    {
        _ = SendLayoutSnapshotAsync(snapshot);
        LayoutChanged?.Invoke(this, snapshot);
        BroadcastCatalog();
        // UI-initiated intents (focus a column, close, collapse) change the
        // focused workspace without going through ActivateAsync — sync the
        // single active workspace from the focused column.
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
        // Focusing a workspace acknowledges its attention.
        bool cleared;
        lock (_sync)
            cleared = _attention.Remove(workspace.WorkspaceId);

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
        else if (workspace.Kind == WorkspaceKind.DshWeb)
        {
            await _dsh.ActivateAsync(workspace.WorkspaceId).ConfigureAwait(false);
            await _bridge.SendEventAsync(new
            {
                type = "workspace_activated",
                workspaceId = workspace.WorkspaceId,
                kind = "dsh_web"
            }).ConfigureAwait(false);
        }
        else
        {
            await _agents.ActivateAsync(workspace.WorkspaceId).ConfigureAwait(false);
        }

        if (cleared)
            BroadcastLayout();
    }

    private Task SendLayoutSnapshotAsync(WorkspaceLayoutSnapshot snapshot)
    {
        Dictionary<Guid, string?> directories;
        lock (_sync)
        {
            directories = _workspaces.ToDictionary(w => w.WorkspaceId, w => w.WorkingDirectory);
        }

        // Two visible agents sharing one git top-level get a non-blocking
        // warning flag: only each column's active tab counts. An inactive tab
        // in the same column sharing a worktree is intentional — switching to
        // it may edge-trigger the notice.
        var agentRoots = new Dictionary<Guid, string>();
        foreach (var column in snapshot.Columns)
        {
            if (!column.ActiveTabId.HasValue)
                continue;
            var active = column.Tabs.FirstOrDefault(tab => tab.WorkspaceId == column.ActiveTabId.Value);
            if (active is not { } activeTab)
                continue;
            if (activeTab.Kind != WorkspaceKind.Agent)
                continue;
            if (!directories.TryGetValue(activeTab.WorkspaceId, out var cwd))
                continue;
            var root = GitRootResolver.Resolve(cwd);
            if (!string.IsNullOrEmpty(root))
                agentRoots[activeTab.WorkspaceId] = root;
        }
        var sharedRoots = agentRoots
            .GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] newConflicts;
        lock (_sync)
        {
            newConflicts = sharedRoots.Except(_sharedWorktreeConflicts, StringComparer.OrdinalIgnoreCase).ToArray();
            _sharedWorktreeConflicts.IntersectWith(sharedRoots);
            _sharedWorktreeConflicts.UnionWith(sharedRoots);
        }

        return SendLayoutAndNoticesAsync(new
        {
            type = "workspace_layout",
            revision = snapshot.LayoutRevision,
            focusedColumnId = snapshot.FocusedColumnId,
            columns = snapshot.Columns.Select(column => new
            {
                columnId = column.ColumnId,
                tabs = column.Tabs.Select(tab => new
                {
                    workspaceId = tab.WorkspaceId,
                    kind = WorkspaceWireKind.ToWire(tab.Kind)
                }).ToArray(),
                activeTabId = column.ActiveTabId,
                ratio = column.Ratio
            }).ToArray()
        }, newConflicts);
    }

    private async Task SendLayoutAndNoticesAsync(object layoutMessage, IReadOnlyList<string> newConflicts)
    {
        await _bridge.SendEventAsync(layoutMessage).ConfigureAwait(false);
        foreach (var _ in newConflicts)
            await SendNoticeAsync("多个可见 Agent 正在使用同一 worktree，请留意并发修改冲突。").ConfigureAwait(false);
    }

    private Task SendNoticeAsync(string message) =>
        _bridge.SendEventAsync(new { type = "workspace_notice", message });

    private void BroadcastCatalog()
    {
        WorkspaceDescriptor[] workspaces;
        Dictionary<Guid, string> attention;
        long revision;
        lock (_sync)
        {
            workspaces = _workspaces.ToArray();
            attention = new Dictionary<Guid, string>(_attention);
            revision = ++_catalogRevision;
        }

        var layout = _layout.Snapshot;
        var placementByWorkspace = new Dictionary<Guid, (string ColumnId, bool IsActiveTab)>();
        foreach (var column in layout.Columns)
        {
            foreach (var tab in column.Tabs)
                placementByWorkspace[tab.WorkspaceId] = (column.ColumnId, tab.WorkspaceId == column.ActiveTabId);
        }
        _ = _bridge.SendEventAsync(new
        {
            type = "workspace_catalog",
            revision,
            maxWorkspaces = 5,
            maxColumns = WorkspaceLayoutService.MaxColumns,
            providers = AgentProviders.Select(provider => new
            {
                key = provider.Key,
                displayName = provider.DisplayName,
                iconKey = provider.IconKey,
                isDefault = provider.IsDefault
            }).ToArray(),
            workspaces = workspaces.Select(workspace =>
            {
                placementByWorkspace.TryGetValue(workspace.WorkspaceId, out var placement);
                attention.TryGetValue(workspace.WorkspaceId, out var attentionKind);
                var blockedReason = _layout.GetSplitBlockedReason(workspace.WorkspaceId);
                return new
                {
                    workspaceId = workspace.WorkspaceId,
                    kind = WorkspaceWireKind.ToWire(workspace.Kind),
                    title = workspace.Title,
                    iconKey = workspace.IconKey,
                    providerKey = workspace.ProviderKey,
                    providerName = workspace.ProviderName,
                    columnId = placement.ColumnId,
                    isActiveTab = placement.IsActiveTab,
                    attentionKind,
                    canSplitRight = blockedReason == null,
                    splitBlockedReason = blockedReason,
                    canCollapse = layout.Columns.Count > 1
                };
            }).ToArray()
        });
    }

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
        _terminalTabs.PaneRatiosRequested -= OnPaneRatiosRequested;
        _terminalTabs.PaneMoveRequested -= OnPaneMoveRequested;
        _terminalTabs.WorkspaceLayoutIntentRequested -= OnWorkspaceLayoutIntentRequested;
        _terminalTabs.WorkspaceCreateRequested -= OnWorkspaceCreateRequested;
        _terminalTabs.DshCommandRequested -= OnDshCommandRequested;
        _agents.WorkspaceCreated -= OnAgentCreated;
        _agents.WorkspaceChanged -= OnAgentChanged;
        _agents.WorkspaceClosed -= OnAgentClosed;
        _agents.WorkspaceActivationRequested -= OnActivationRequested;
        _dsh.WorkspaceCreated -= OnDshCreated;
        _dsh.WorkspaceClosed -= OnDshClosed;
    }
}
