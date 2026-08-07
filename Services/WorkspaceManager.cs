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
    /// <summary>Move the focus by one pane (keyboard shortcut path).</summary>
    void FocusAdjacentPane(int delta);
    /// <summary>Exchange the two panes' workspace assignments.</summary>
    void SwapPanes();
    /// <summary>Collapse back to a single pane; other workspaces go background.</summary>
    void CollapseToSinglePane();
    /// <summary>Divider drag end: set one pane's width share.</summary>
    bool SetPaneRatios(long baseRevision, IReadOnlyDictionary<string, double> ratios);
    /// <summary>Toggle presentation-only zoom on the focused pane.</summary>
    void TogglePaneZoom();
    /// <summary>Creation transaction: the next created workspace lands here
    /// (<see cref="WorkspaceLayoutService.NewPanePlacement"/> for a fresh pane).</summary>
    void RecordPendingPlacement(string paneId);
    /// <summary>Tab badge source: needs attention and not focused-visible.</summary>
    bool IsAttentionNeeded(Guid workspaceId);
    /// <summary>Current requested-layout snapshot (panes, focus, ratios).</summary>
    WorkspaceLayoutSnapshot LayoutSnapshot { get; }
    /// <summary>Requested layout changed (pane assignment, focus, ratios).</summary>
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
        _terminalTabs.PaneFocusRequested += (_, paneId) => _layout.FocusPane(paneId);
        _terminalTabs.PaneRatiosRequested += OnPaneRatiosRequested;
        _terminalTabs.PaneMoveRequested += OnPaneMoveRequested;
        _terminalTabs.WorkspaceLayoutIntentRequested += OnWorkspaceLayoutIntentRequested;
        _terminalTabs.WorkspaceCreateRequested += OnWorkspaceCreateRequested;
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
        if (!_layout.SplitWorkspaceToNewPane(workspaceId, workspace.Kind))
            BroadcastCatalog();
    }

    public void FocusPane(string paneId) => _layout.FocusPane(paneId);

    public void FocusAdjacentPane(int delta) => _layout.FocusAdjacentPane(delta);

    public void SwapPanes() => _layout.SwapPanes();

    public void CollapseToSinglePane() => _layout.CollapseToSinglePane();

    public bool SetPaneRatios(long baseRevision, IReadOnlyDictionary<string, double> ratios) =>
        _layout.SetPaneRatios(baseRevision, ratios);

    public void RecordPendingPlacement(string paneId) => _layout.RecordPendingPlacement(paneId);

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
    /// pane is unfocused. Focusing the pane acknowledges; returning to
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
        // The layout collapses the vacated pane or pulls the MRU background
        // workspace in atomically; its changed event activates the new focus.
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
        _layout.MoveWorkspaceToPane(args.WorkspaceId, workspace.Kind, args.PaneId);
    }

    private void OnPaneRatiosRequested(object? sender, PaneRatiosEventArgs args)
    {
        if (!_layout.SetPaneRatios(args.BaseRevision, args.Ratios))
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
            case "move_to_pane" when args.WorkspaceId.HasValue && !string.IsNullOrWhiteSpace(args.PaneId):
                OnPaneMoveRequested(this, new PaneMoveEventArgs
                {
                    WorkspaceId = args.WorkspaceId.Value,
                    PaneId = args.PaneId
                });
                break;
            case "swap":
                SwapPanes();
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

        if (args.Placement == "new_right")
        {
            if (_layout.Snapshot.Panes.Count >= WorkspaceLayoutService.MaxPanes)
            {
                await SendNoticeAsync("最多支持 4 列").ConfigureAwait(false);
                return;
            }
            _layout.RecordPendingPlacement(WorkspaceLayoutService.NewPanePlacement);
        }

        var created = args.Kind == "terminal"
            ? await CreateTerminalAsync().ConfigureAwait(false)
            : await CreateAgentAsync(args.ProviderKey!).ConfigureAwait(false);
        if (!created.HasValue && args.Placement == "new_right")
            _layout.CancelPendingPlacement();
    }

    private void OnLayoutChanged(object? sender, WorkspaceLayoutSnapshot snapshot)
    {
        _ = SendLayoutSnapshotAsync(snapshot);
        LayoutChanged?.Invoke(this, snapshot);
        BroadcastCatalog();
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
        // warning flag: split panes are not safe parallel development.
        var agentRoots = new Dictionary<Guid, string>();
        foreach (var pane in snapshot.Panes)
        {
            if (pane.Kind != WorkspaceKind.Agent || !pane.WorkspaceId.HasValue)
                continue;
            if (!directories.TryGetValue(pane.WorkspaceId.Value, out var cwd))
                continue;
            var root = GitRootResolver.Resolve(cwd);
            if (!string.IsNullOrEmpty(root))
                agentRoots[pane.WorkspaceId.Value] = root;
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
            focusedPaneId = snapshot.FocusedPaneId,
            panes = snapshot.Panes.Select(p => new
            {
                paneId = p.PaneId,
                workspaceId = p.WorkspaceId,
                kind = p.Kind?.ToString().ToLowerInvariant(),
                ratio = p.Ratio
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
        var paneByWorkspace = layout.Panes
            .Where(pane => pane.WorkspaceId.HasValue)
            .ToDictionary(pane => pane.WorkspaceId!.Value, pane => pane.PaneId);
        var paneIds = layout.Panes.Select(pane => pane.PaneId).ToArray();
        _ = _bridge.SendEventAsync(new
        {
            type = "workspace_catalog",
            revision,
            maxWorkspaces = 5,
            maxPanes = WorkspaceLayoutService.MaxPanes,
            providers = AgentProviders.Select(provider => new
            {
                key = provider.Key,
                displayName = provider.DisplayName,
                iconKey = provider.IconKey,
                isDefault = provider.IsDefault
            }).ToArray(),
            workspaces = workspaces.Select(workspace =>
            {
                paneByWorkspace.TryGetValue(workspace.WorkspaceId, out var paneId);
                attention.TryGetValue(workspace.WorkspaceId, out var attentionKind);
                var blockedReason = _layout.GetSplitBlockedReason(workspace.WorkspaceId);
                return new
                {
                    workspaceId = workspace.WorkspaceId,
                    kind = workspace.Kind.ToString().ToLowerInvariant(),
                    title = workspace.Title,
                    iconKey = workspace.IconKey,
                    providerKey = workspace.ProviderKey,
                    providerName = workspace.ProviderName,
                    paneId,
                    attentionKind,
                    canSplitRight = blockedReason == null,
                    splitBlockedReason = blockedReason,
                    availableTargetPanes = paneIds
                        .Select((id, index) => new { paneId = id, column = index + 1 })
                        .Where(entry => !string.Equals(entry.paneId, paneId, StringComparison.Ordinal))
                        .ToArray(),
                    canCollapse = layout.Panes.Count > 1,
                    canSwap = layout.Panes.Count == 2
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
        _agents.WorkspaceCreated -= OnAgentCreated;
        _agents.WorkspaceChanged -= OnAgentChanged;
        _agents.WorkspaceClosed -= OnAgentClosed;
        _agents.WorkspaceActivationRequested -= OnActivationRequested;
    }
}
