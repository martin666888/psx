using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Window-level, in-memory truth source for the pane layout (split panes).
/// Owns the canonical <b>requested</b> layout: ordered pane slots, workspace
/// assignment, the focused pane and normalized ratios, with a monotonically
/// increasing revision on every snapshot. The WebView renders the
/// <b>effective</b> presentation (temporary narrow-width collapses, terminal
/// fit) which never writes back here.
///
/// Invariants: panes are never empty (a vacated pane collapses unless it is
/// the last one); a workspace occupies at most one pane; the focused pane
/// always exists while any pane does. Activation is three states — visible
/// (assigned to a pane), focused (its pane receives the keyboard) and
/// selected (a Tab click, which assigns into the focused pane or, when the
/// workspace is already visible elsewhere, focuses that pane instead).
/// </summary>
public sealed class WorkspaceLayoutService
{
    /// <summary>Phase 1 allows two columns; Phase 2 raises this.</summary>
    public const int MaxPanes = 2;
    public const string FirstPaneId = "pane-1";

    private sealed class PaneState
    {
        public required string PaneId { get; init; }
        public Guid? WorkspaceId { get; set; }
        public WorkspaceKind? Kind { get; set; }
        public double Ratio { get; set; } = 1.0;
    }

    private readonly object _sync = new();
    private readonly List<PaneState> _panes = new();
    private readonly List<(Guid Id, WorkspaceKind Kind)> _mru = new();
    private readonly Queue<string> _pendingPlacements = new();
    private string _focusedPaneId = FirstPaneId;
    private long _revision;
    private int _nextPaneNumber = 2;

    public WorkspaceLayoutService()
    {
        _panes.Add(new PaneState { PaneId = FirstPaneId });
    }

    public event EventHandler<WorkspaceLayoutSnapshot>? LayoutChanged;

    public WorkspaceLayoutSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotLocked();
        }
    }

    /// <summary>The focused pane's workspace — the single "active workspace"
    /// every legacy consumer (TabBar IsActive, brokers) derives from.</summary>
    public Guid? FocusedWorkspaceId
    {
        get
        {
            lock (_sync)
                return _panes.FirstOrDefault(p => p.PaneId == _focusedPaneId)?.WorkspaceId;
        }
    }

    /// <summary>Placement token for「+ 新建到新列」: the pane is created
    /// atomically at assignment time so it never renders empty.</summary>
    public const string NewPanePlacement = "pane-new";

    /// <summary>Remember the user's target pane for the workspace being
    /// created; the next assignment consumes it (creation transaction). Pass
    /// <see cref="NewPanePlacement"/> to create a fresh right-hand pane.</summary>
    public void RecordPendingPlacement(string paneId)
    {
        lock (_sync)
            _pendingPlacements.Enqueue(paneId);
    }

    /// <summary>Tab click / activation: an already-visible workspace focuses
    /// its pane (never duplicated); anything else replaces the focused pane's
    /// content. Consumes a pending placement when one was recorded.</summary>
    public void AssignActiveWorkspace(Guid workspaceId, WorkspaceKind kind)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            TouchMruLocked(workspaceId, kind);

            var existingPane = _panes.FirstOrDefault(p => p.WorkspaceId == workspaceId);
            if (existingPane != null)
            {
                _pendingPlacements.Clear();
                if (_focusedPaneId == existingPane.PaneId)
                    return;
                _focusedPaneId = existingPane.PaneId;
                changed = BumpRevisionLocked();
            }
            else
            {
                var target = ConsumePlacementLocked();
                var pane = _panes.FirstOrDefault(p => p.PaneId == target) ?? FocusedPaneLocked();
                var mutated = pane.WorkspaceId != workspaceId || pane.Kind != kind || _focusedPaneId != pane.PaneId;
                if (!mutated)
                    return;
                pane.WorkspaceId = workspaceId;
                pane.Kind = kind;
                _focusedPaneId = pane.PaneId;
                changed = BumpRevisionLocked();
            }
        }
        LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Move a workspace into a fresh right-hand pane and focus it.
    /// Falls back to a plain assignment at the column cap.</summary>
    public void SplitWorkspaceToNewPane(Guid workspaceId, WorkspaceKind kind)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_panes.Count >= MaxPanes)
            {
                var visiblePane = _panes.FirstOrDefault(p => p.WorkspaceId == workspaceId);
                if (visiblePane != null)
                {
                    // At the cap, splitting an already-visible workspace
                    // degrades to the visible-tab rule: just focus its pane.
                    if (_focusedPaneId != visiblePane.PaneId)
                    {
                        _focusedPaneId = visiblePane.PaneId;
                        changed = BumpRevisionLocked();
                    }
                }
                else
                {
                    // At the cap a split request for a background workspace
                    // degrades to assigning into the focused pane.
                    TouchMruLocked(workspaceId, kind);
                    var pane = FocusedPaneLocked();
                    pane.WorkspaceId = workspaceId;
                    pane.Kind = kind;
                    changed = BumpRevisionLocked();
                }
            }
            else
            {
                TouchMruLocked(workspaceId, kind);
                VacateWorkspaceLocked(workspaceId);
                var pane = new PaneState { PaneId = $"pane-{_nextPaneNumber++}", WorkspaceId = workspaceId, Kind = kind };
                _panes.Add(pane);
                NormalizeRatiosLocked();
                _focusedPaneId = pane.PaneId;
                changed = BumpRevisionLocked();
            }
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Focus a pane without changing any assignment (click inside a
    /// pane, or clicking a Tab whose workspace is already visible there).</summary>
    public void FocusPane(string paneId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_focusedPaneId == paneId || _panes.All(p => p.PaneId != paneId))
                return;
            _focusedPaneId = paneId;
            var workspace = _panes.First(p => p.PaneId == paneId);
            if (workspace.WorkspaceId.HasValue && workspace.Kind.HasValue)
                TouchMruLocked(workspace.WorkspaceId.Value, workspace.Kind.Value);
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Move the focus by one pane (wraps). Keyboard shortcut path.</summary>
    public void FocusAdjacentPane(int delta)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_panes.Count < 2)
                return;
            var index = _panes.FindIndex(p => p.PaneId == _focusedPaneId);
            var next = _panes[((index + delta) % _panes.Count + _panes.Count) % _panes.Count];
            _focusedPaneId = next.PaneId;
            if (next.WorkspaceId.HasValue && next.Kind.HasValue)
                TouchMruLocked(next.WorkspaceId.Value, next.Kind.Value);
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Exchange the workspace assignments of the two panes (the
    /// focus stays on the same pane slot).</summary>
    public void SwapPanes()
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_panes.Count != 2)
                return;
            (_panes[0].WorkspaceId, _panes[1].WorkspaceId) = (_panes[1].WorkspaceId, _panes[0].WorkspaceId);
            (_panes[0].Kind, _panes[1].Kind) = (_panes[1].Kind, _panes[0].Kind);
            changed = BumpRevisionLocked();
        }
        LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Collapse back to one pane; the focused pane survives and its
    /// content stays, every other workspace goes background.</summary>
    public void CollapseToSinglePane()
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_panes.Count <= 1)
                return;
            var survivor = _panes.FirstOrDefault(p => p.PaneId == _focusedPaneId) ?? _panes[0];
            _panes.RemoveAll(p => p != survivor);
            survivor.Ratio = 1.0;
            _focusedPaneId = survivor.PaneId;
            changed = BumpRevisionLocked();
        }
        LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Divider drag end: set one pane's share; ratios renormalize.</summary>
    public void SetPaneRatio(string paneId, double ratio)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            var pane = _panes.FirstOrDefault(p => p.PaneId == paneId);
            if (pane == null || _panes.Count < 2)
                return;
            var clamped = Math.Clamp(ratio, 0.2, 0.8);
            if (Math.Abs(pane.Ratio - clamped) < 0.0001)
                return;
            pane.Ratio = clamped;
            NormalizeRatiosLocked(except: pane);
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Close: the pane holding the workspace collapses (remaining
    /// panes renormalize); a lone pane pulls the most-recent background
    /// workspace into the same snapshot; the focus falls to the left
    /// neighbor, else to whatever remains.</summary>
    public void RemoveWorkspace(Guid workspaceId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            var mruIndex = _mru.FindIndex(entry => entry.Id == workspaceId);
            if (mruIndex >= 0)
                _mru.RemoveAt(mruIndex);

            var pane = _panes.FirstOrDefault(p => p.WorkspaceId == workspaceId);
            if (pane == null)
            {
                // A background workspace leaving does not change the visible
                // layout — no snapshot.
                return;
            }

            pane.WorkspaceId = null;
            pane.Kind = null;
            if (_panes.Count > 1)
            {
                var index = _panes.IndexOf(pane);
                _panes.RemoveAt(index);
                NormalizeRatiosLocked();
                if (_focusedPaneId == pane.PaneId)
                {
                    var fallback = _panes[Math.Max(0, index - 1)];
                    _focusedPaneId = fallback.PaneId;
                }
            }
            else
            {
                // Last pane: pull the most recent background workspace in
                // atomically so the pane never renders empty.
                var replacement = _mru.FirstOrDefault();
                if (replacement.Id != Guid.Empty)
                {
                    pane.WorkspaceId = replacement.Id;
                    pane.Kind = replacement.Kind;
                }
            }
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    // --- internals (all under _sync) -----------------------------------------

    private PaneState FocusedPaneLocked() =>
        _panes.FirstOrDefault(p => p.PaneId == _focusedPaneId) ?? _panes[0];

    private string ConsumePlacementLocked()
    {
        if (_pendingPlacements.Count == 0)
            return _focusedPaneId;
        var target = _pendingPlacements.Dequeue();
        if (target == NewPanePlacement)
        {
            if (_panes.Count >= MaxPanes)
                return _focusedPaneId;
            var created = new PaneState { PaneId = $"pane-{_nextPaneNumber++}" };
            _panes.Add(created);
            NormalizeRatiosLocked();
            return created.PaneId;
        }
        return _panes.Any(p => p.PaneId == target) ? target : _focusedPaneId;
    }

    private void TouchMruLocked(Guid workspaceId, WorkspaceKind kind)
    {
        _mru.RemoveAll(entry => entry.Id == workspaceId);
        _mru.Insert(0, (workspaceId, kind));
    }

    private void VacateWorkspaceLocked(Guid workspaceId)
    {
        var source = _panes.FirstOrDefault(p => p.WorkspaceId == workspaceId);
        if (source == null)
            return;
        _panes.Remove(source);
        NormalizeRatiosLocked();
        if (_focusedPaneId == source.PaneId)
            _focusedPaneId = _panes[0].PaneId;
    }

    private void NormalizeRatiosLocked(PaneState? except = null)
    {
        if (_panes.Count == 0)
            return;
        if (except == null)
        {
            var share = 1.0 / _panes.Count;
            foreach (var pane in _panes)
                pane.Ratio = share;
            return;
        }
        var rest = _panes.Where(p => p != except).ToArray();
        var remaining = Math.Max(0.0, 1.0 - except.Ratio);
        var restShare = rest.Length > 0 ? remaining / rest.Length : 0.0;
        foreach (var pane in rest)
            pane.Ratio = restShare;
    }

    private WorkspaceLayoutSnapshot BumpRevisionLocked()
    {
        _revision++;
        return CreateSnapshotLocked();
    }

    private WorkspaceLayoutSnapshot CreateSnapshotLocked() =>
        new(
            _revision,
            _panes
                .Select(p => new WorkspacePaneSnapshot(p.PaneId, p.WorkspaceId, p.Kind, Math.Round(p.Ratio, 4)))
                .ToArray(),
            _focusedPaneId);
}
