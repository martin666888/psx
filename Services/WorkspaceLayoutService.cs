using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Window-level, in-memory truth source for the workspace layout (ordered
/// columns of tabs — the VS Code editor-group model). Owns the canonical
/// <b>requested</b> layout: columns with a tab stack each, the active tab per
/// column, the focused column and normalized ratios, with a monotonically
/// increasing revision on every snapshot. The WebView renders the
/// <b>effective</b> presentation (a single pure-ratio allocation; pixel
/// floors constrain only the drag clamp and sash states) which never writes
/// back here.
///
/// Invariants: every open workspace is exactly one tab in exactly one column
/// (there is no "background" set); a column whose last tab leaves is
/// destroyed and the remaining columns absorb its ratio; the focused column
/// always exists. Closing the unique tab of the only column leaves one fresh
/// empty column — the caller creates a replacement terminal tab (never a MRU
/// resurrection). One empty column is the initial startup state too.
/// </summary>
public sealed class WorkspaceLayoutService
{
    /// <summary>Column cap: one column is the primary form, 2/3 columns are
    /// explicit splits; a new-column placement at the cap degrades into a tab
    /// in the focused column instead.</summary>
    public const int MaxColumns = 3;
    public const string FirstColumnId = "column-1";

    private sealed class ColumnState
    {
        public required string ColumnId { get; init; }
        public List<(Guid Id, WorkspaceKind Kind)> Tabs { get; } = new();
        public Guid ActiveTabId { get; set; }
        public double Ratio { get; set; } = 1.0;
    }

    private readonly object _sync = new();
    private readonly List<ColumnState> _columns = new();
    private readonly Queue<string> _pendingPlacements = new();
    private string _focusedColumnId = FirstColumnId;
    private long _revision;
    private int _nextColumnNumber = 2;

    public WorkspaceLayoutService()
    {
        _columns.Add(new ColumnState { ColumnId = FirstColumnId });
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

    /// <summary>The focused column's active tab — the single "active
    /// workspace" every legacy consumer (TabBar IsActive, brokers) derives
    /// from. Null while the sole column is empty (startup, or the moment
    /// before the caller fills the replacement terminal).</summary>
    public Guid? FocusedWorkspaceId
    {
        get
        {
            lock (_sync)
            {
                var focused = FocusedColumnLocked();
                return focused.ActiveTabId == Guid.Empty ? null : focused.ActiveTabId;
            }
        }
    }

    /// <summary>Placement token for「右侧新列」: the column is created
    /// atomically at assignment time so it never renders empty.</summary>
    public const string NewPanePlacement = "pane-new";

    /// <summary>Remember the user's target column for the workspace being
    /// created; the next assignment consumes it (creation transaction). Pass
    /// <see cref="NewPanePlacement"/> to create a fresh right-hand column.</summary>
    public void RecordPendingPlacement(string paneId)
    {
        lock (_sync)
            _pendingPlacements.Enqueue(paneId);
    }

    public void CancelPendingPlacement()
    {
        lock (_sync)
            _pendingPlacements.Clear();
    }

    /// <summary>Activation (workspace-list click, History click, tab click).
    /// An already-open workspace — by the invariant a tab in some column —
    /// activates that tab and focuses its column (pure jump, never duplicated,
    /// never moved). Anything else opens as a new tab: the default placement
    /// appends to the focused column; a pending <see cref="NewPanePlacement"/>
    /// creates a fresh right-hand column unless the column cap is already
    /// reached, in which case it degrades into a tab in the focused column.</summary>
    public void AssignActiveWorkspace(Guid workspaceId, WorkspaceKind kind)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            var existing = FindTabLocked(workspaceId);
            if (existing != null)
            {
                // Pure jump: the tab stays in its column, the column focuses.
                _pendingPlacements.Clear();
                if (_focusedColumnId == existing.Value.Column.ColumnId
                    && existing.Value.Column.ActiveTabId == workspaceId)
                    return;
                existing.Value.Column.ActiveTabId = workspaceId;
                _focusedColumnId = existing.Value.Column.ColumnId;
                changed = BumpRevisionLocked();
            }
            else
            {
                var column = ConsumePlacementLocked();
                column.Tabs.Add((workspaceId, kind));
                column.ActiveTabId = workspaceId;
                _focusedColumnId = column.ColumnId;
                changed = BumpRevisionLocked();
            }
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Move a workspace's tab into a fresh right-hand column and
    /// focus it. The source column keeps its remaining tabs (its active tab
    /// falls to the left neighbor when the moved tab was active); an emptied
    /// source column collapses and the new column lands one position right of
    /// the collapsed column's original slot. Returns false — without changing
    /// anything — at the column cap (a sole-tab source column collapsing there
    /// adds no column and stays allowed), or when the layout holds the single
    /// open workspace (splitting it cannot produce a second column).</summary>
    public bool SplitWorkspaceToNewPane(Guid workspaceId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            // Splitting the only tab in the only column leaves one column (the
            // source collapses and the new column replaces it) — a no-op shape.
            if (_columns.Count == 1 && _columns[0].Tabs.Count == 1 && _columns[0].Tabs[0].Id == workspaceId)
                return false;
            var found = FindTabLocked(workspaceId);
            // The column cap blocks only splits that add a column: a sole-tab
            // source column collapses, so the column count stays unchanged.
            if (_columns.Count >= MaxColumns && !(found.HasValue && found.Value.Column.Tabs.Count == 1))
                return false;
            var insertAt = found.HasValue
                ? _columns.IndexOf(found.Value.Column)
                : _columns.IndexOf(FocusedColumnLocked());
            WorkspaceKind kind;
            if (found.HasValue)
            {
                kind = found.Value.Column.Tabs[found.Value.Index].Kind;
                RemoveTabFromColumnLocked(found.Value.Column, found.Value.Index);
                if (found.Value.Column.Tabs.Count == 0)
                {
                    // The collapsed column is gone; the new column lands one
                    // position right of its original index, clamped to the end
                    // (never the collapsed slot itself).
                    _columns.Remove(found.Value.Column);
                    insertAt = Math.Min(insertAt + 1, _columns.Count);
                }
                else
                {
                    insertAt++;
                }
            }
            else
            {
                // Unreachable under the invariant; defensively treat as a
                // fresh tab in a new column right of the focused one.
                kind = WorkspaceKind.Terminal;
                insertAt++;
            }

            var column = new ColumnState { ColumnId = $"column-{_nextColumnNumber++}" };
            column.Tabs.Add((workspaceId, kind));
            column.ActiveTabId = workspaceId;
            _columns.Insert(Math.Min(insertAt, _columns.Count), column);
            NormalizeRatiosLocked();
            _focusedColumnId = column.ColumnId;
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
        return changed != null;
    }

    public string? GetSplitBlockedReason(Guid workspaceId)
    {
        lock (_sync)
        {
            if (_columns.Count == 1 && _columns[0].Tabs.Count == 1 && _columns[0].Tabs[0].Id == workspaceId)
                return "只有一个工作区，无法拆分";
            // The 3-column cap blocks only splits that add a column; a sole-tab
            // source column collapses and the count stays put.
            var found = FindTabLocked(workspaceId);
            if (_columns.Count >= MaxColumns && !(found.HasValue && found.Value.Column.Tabs.Count == 1))
                return "最多支持 3 列";
            return null;
        }
    }

    /// <summary>Focus a column without changing any assignment (click inside
    /// a pane, or clicking a Tab whose workspace is already its active tab).
    /// The id space is the column id — the <c>pane_focus</c> wire name is
    /// preserved by contract.</summary>
    public void FocusPane(string columnId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_focusedColumnId == columnId || _columns.All(c => c.ColumnId != columnId))
                return;
            _focusedColumnId = columnId;
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Move the focus by one column (wraps). Keyboard shortcut path.</summary>
    public void FocusAdjacentPane(int delta)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_columns.Count < 2)
                return;
            var index = _columns.FindIndex(c => c.ColumnId == _focusedColumnId);
            var next = _columns[((index + delta) % _columns.Count + _columns.Count) % _columns.Count];
            _focusedColumnId = next.ColumnId;
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Drag a tab onto a column: the tab moves into the target
    /// column (becoming its active tab) and the column takes the focus. No
    /// swap semantics — the target column never loses a tab. Dropping a tab
    /// back onto its own column just activates it.</summary>
    public void MoveWorkspaceToColumn(Guid workspaceId, string columnId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            var target = _columns.FirstOrDefault(c => c.ColumnId == columnId);
            if (target == null)
                return;
            var found = FindTabLocked(workspaceId);
            if (found == null)
            {
                // Unreachable under the invariant (drag sources are always
                // open tabs); focus the target column defensively.
                if (_focusedColumnId != target.ColumnId)
                {
                    _focusedColumnId = target.ColumnId;
                    changed = BumpRevisionLocked();
                }
                return;
            }
            if (found.Value.Column == target)
            {
                if (_focusedColumnId != target.ColumnId || target.ActiveTabId != workspaceId)
                {
                    target.ActiveTabId = workspaceId;
                    _focusedColumnId = target.ColumnId;
                    changed = BumpRevisionLocked();
                }
                return;
            }

            var kind = found.Value.Column.Tabs[found.Value.Index].Kind;
            RemoveTabFromColumnLocked(found.Value.Column, found.Value.Index);
            if (found.Value.Column.Tabs.Count == 0)
                AbsorbRatioLocked(found.Value.Column);
            target.Tabs.Add((workspaceId, kind));
            target.ActiveTabId = workspaceId;
            _focusedColumnId = target.ColumnId;
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Merge the layout into a single column: every other column's
    /// tabs join the focused column's tab stack tail, in original column
    /// order; the focused column keeps its own active tab. No workspace leaves
    /// the open set.</summary>
    public void CollapseToSinglePane()
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_columns.Count <= 1)
                return;
            var focused = FocusedColumnLocked();
            foreach (var column in _columns.ToArray())
            {
                if (column == focused)
                    continue;
                focused.Tabs.AddRange(column.Tabs);
                _columns.Remove(column);
            }
            focused.Ratio = 1.0;
            _focusedColumnId = focused.ColumnId;
            changed = BumpRevisionLocked();
        }
        LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>Atomically commit the complete column ratio vector produced by
    /// one divider drag. Pixel minimums belong to the WebView presentation;
    /// this logical truth source validates identity, revision and normalization.</summary>
    public bool SetPaneRatios(long baseRevision, IReadOnlyDictionary<string, double> ratios)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (baseRevision != _revision || _columns.Count < 2 || ratios.Count != _columns.Count)
                return false;
            if (_columns.Any(c => !ratios.TryGetValue(c.ColumnId, out var ratio)
                                  || !double.IsFinite(ratio)
                                  || ratio <= 0))
                return false;
            var sum = ratios.Values.Sum();
            if (!double.IsFinite(sum) || Math.Abs(sum - 1.0) > 0.0001)
                return false;
            if (_columns.All(c => Math.Abs(c.Ratio - ratios[c.ColumnId]) < 0.0001))
                return true;
            foreach (var column in _columns)
                column.Ratio = ratios[column.ColumnId] / sum;
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
        return true;
    }

    /// <summary>Bump the revision without touching any layout state and return
    /// the fresh snapshot. Acknowledges a rejected pane-ratio commit so the
    /// WebView's revision guard accepts the follow-up broadcast. This method
    /// itself never raises <see cref="LayoutChanged"/> — broadcasting the
    /// returned snapshot is the caller's responsibility.</summary>
    public WorkspaceLayoutSnapshot TouchRevision()
    {
        lock (_sync)
            return BumpRevisionLocked();
    }

    /// <summary>Close a tab: the column keeps its other tabs and its active
    /// tab falls to the left neighbor (else the right) when the closed tab
    /// was active. An emptied column is destroyed and the remaining columns
    /// absorb its ratio proportional to their own (user-dragged proportions
    /// survive), the focus falling to the left neighbor column (else whatever
    /// remains). Closing the unique tab of the only column leaves a fresh
    /// empty column — the caller creates a replacement terminal tab.</summary>
    public void RemoveWorkspace(Guid workspaceId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            var found = FindTabLocked(workspaceId);
            if (found == null)
                return;

            var destroyedIndex = _columns.IndexOf(found.Value.Column);
            RemoveTabFromColumnLocked(found.Value.Column, found.Value.Index);
            if (found.Value.Column.Tabs.Count == 0)
            {
                if (_columns.Count == 1)
                {
                    // Unique column unique tab: reset to the startup shape.
                    // The caller creates a fresh terminal tab; nothing is
                    // resurrected from a MRU.
                    _columns.Clear();
                    _columns.Add(new ColumnState { ColumnId = FirstColumnId });
                    _focusedColumnId = FirstColumnId;
                }
                else
                {
                    AbsorbRatioLocked(found.Value.Column);
                    if (_focusedColumnId == found.Value.Column.ColumnId)
                    {
                        var fallback = _columns[Math.Max(0, destroyedIndex - 1)];
                        _focusedColumnId = fallback.ColumnId;
                    }
                }
            }
            changed = BumpRevisionLocked();
        }
        if (changed != null)
            LayoutChanged?.Invoke(this, changed);
    }

    // --- internals (all under _sync) -----------------------------------------

    private ColumnState FocusedColumnLocked() =>
        _columns.FirstOrDefault(c => c.ColumnId == _focusedColumnId) ?? _columns[0];

    private (ColumnState Column, int Index)? FindTabLocked(Guid workspaceId)
    {
        foreach (var column in _columns)
        {
            var index = column.Tabs.FindIndex(tab => tab.Id == workspaceId);
            if (index >= 0)
                return (column, index);
        }
        return null;
    }

    private void RemoveTabFromColumnLocked(ColumnState column, int index)
    {
        var removed = column.Tabs[index].Id;
        column.Tabs.RemoveAt(index);
        if (column.ActiveTabId != removed)
            return;
        // Active tab prefers the left neighbor, else the right.
        column.ActiveTabId = index > 0
            ? column.Tabs[index - 1].Id
            : column.Tabs.Count > 0 ? column.Tabs[0].Id : Guid.Empty;
    }

    private ColumnState ConsumePlacementLocked()
    {
        if (_pendingPlacements.Count == 0)
            return FocusedColumnLocked();
        var target = _pendingPlacements.Dequeue();
        if (target == NewPanePlacement)
        {
            if (_columns.Count >= MaxColumns)
                return FocusedColumnLocked();
            var created = new ColumnState { ColumnId = $"column-{_nextColumnNumber++}" };
            _columns.Add(created);
            NormalizeRatiosLocked();
            return created;
        }
        return _columns.FirstOrDefault(c => c.ColumnId == target) ?? FocusedColumnLocked();
    }

    private void NormalizeRatiosLocked(ColumnState? except = null)
    {
        if (_columns.Count == 0)
            return;
        if (except == null)
        {
            var share = 1.0 / _columns.Count;
            foreach (var column in _columns)
                column.Ratio = share;
            return;
        }
        var rest = _columns.Where(c => c != except).ToArray();
        var remaining = Math.Max(0.0, 1.0 - except.Ratio);
        var restShare = rest.Length > 0 ? remaining / rest.Length : 0.0;
        foreach (var column in rest)
            column.Ratio = restShare;
    }

    /// <summary>Destroy a column and absorb its width share: the destroyed
    /// column's ratio is spread over the remaining columns proportional to
    /// their current ratios (remaining × 1/remainingSum), keeping the total
    /// at one, so user-dragged proportions survive a column close. Falls back
    /// to equal shares when a remaining ratio is zero, non-finite or the
    /// remainder sums to nothing.</summary>
    private void AbsorbRatioLocked(ColumnState destroyed)
    {
        _columns.Remove(destroyed);
        if (_columns.Count == 0)
            return;
        if (_columns.Any(column => !double.IsFinite(column.Ratio) || column.Ratio <= 0))
        {
            NormalizeRatiosLocked();
            return;
        }
        var remainder = _columns.Sum(column => column.Ratio);
        if (!double.IsFinite(remainder) || remainder <= 0.0)
        {
            NormalizeRatiosLocked();
            return;
        }
        foreach (var column in _columns)
            column.Ratio = column.Ratio / remainder;
    }

    private WorkspaceLayoutSnapshot BumpRevisionLocked()
    {
        _revision++;
        return CreateSnapshotLocked();
    }

    private WorkspaceLayoutSnapshot CreateSnapshotLocked()
    {
        AssertInvariantLocked();
        return new WorkspaceLayoutSnapshot(
            _revision,
            _columns
                .Select(c => new WorkspaceColumnSnapshot(
                    c.ColumnId,
                    c.Tabs.Select(tab => new WorkspaceTabSnapshot(tab.Id, tab.Kind)).ToArray(),
                    c.ActiveTabId == Guid.Empty ? null : c.ActiveTabId,
                    Math.Round(c.Ratio, 4)))
                .ToArray(),
            _focusedColumnId);
    }

    /// <summary>Debug-only structural guard for the no-background invariant.
    /// The workspace-set half of the invariant (tabs ∪ = open workspaces) is
    /// enforced by construction in <c>WorkspaceManager</c> — every create
    /// assigns, every close removes through this service.</summary>
    private void AssertInvariantLocked()
    {
        System.Diagnostics.Debug.Assert(_columns.Count >= 1, "layout must always keep at least one column");
        System.Diagnostics.Debug.Assert(
            _columns.Count(c => c.Tabs.Count == 0) == 0 || _columns.Count == 1,
            "only the sole column may be empty");
        var allTabs = _columns.SelectMany(c => c.Tabs).ToList();
        System.Diagnostics.Debug.Assert(
            allTabs.Select(tab => tab.Id).Distinct().Count() == allTabs.Count,
            "a workspace may only exist as one tab in the layout");
        foreach (var column in _columns)
        {
            System.Diagnostics.Debug.Assert(
                column.ActiveTabId == Guid.Empty || column.Tabs.Any(tab => tab.Id == column.ActiveTabId),
                "ActiveTabId must be a tab of its column");
            System.Diagnostics.Debug.Assert(
                column.Tabs.Count > 0 || column.ActiveTabId == Guid.Empty,
                "an empty column must not hold an active tab");
        }
        System.Diagnostics.Debug.Assert(
            _columns.Any(c => c.ColumnId == _focusedColumnId),
            "the focused column must exist");
        var sum = _columns.Sum(c => c.Ratio);
        System.Diagnostics.Debug.Assert(Math.Abs(sum - 1.0) < 0.001, "column ratios must sum to one");
    }
}
