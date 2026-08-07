using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Window-level, in-memory truth source for the pane layout (split panes).
/// Owns the canonical <b>requested</b> layout: pane slots, workspace
/// assignment, the focused pane and normalized ratios, with a monotonically
/// increasing revision on every snapshot. The WebView renders the
/// <b>effective</b> presentation (temporary narrow-width collapses, terminal
/// fit) which never writes back here. Phase 0 keeps exactly one pane whose
/// content is the active workspace and which always holds the focus; the
/// three-state model (visible / focused / selected) therefore degenerates to
/// today's single-active-workspace behavior.
/// </summary>
public sealed class WorkspaceLayoutService
{
    /// <summary>The single pane Phase 0 always reports.</summary>
    public const string SinglePaneId = "pane-1";

    private readonly object _sync = new();
    private long _revision;
    private Guid? _paneWorkspaceId;
    private WorkspaceKind? _paneKind;

    public event EventHandler<WorkspaceLayoutSnapshot>? LayoutChanged;

    public WorkspaceLayoutSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotLocked();
        }
    }

    /// <summary>The active workspace becomes the single pane's content and
    /// takes the focus. No-op when the assignment is unchanged.</summary>
    public void AssignActiveWorkspace(Guid workspaceId, WorkspaceKind kind)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_paneWorkspaceId == workspaceId && _paneKind == kind)
                return;
            _paneWorkspaceId = workspaceId;
            _paneKind = kind;
            changed = BumpRevisionLocked();
        }
        LayoutChanged?.Invoke(this, changed);
    }

    /// <summary>A closed workspace leaves the layout; the pane becomes empty
    /// until the manager activates a replacement.</summary>
    public void RemoveWorkspace(Guid workspaceId)
    {
        WorkspaceLayoutSnapshot? changed = null;
        lock (_sync)
        {
            if (_paneWorkspaceId != workspaceId)
                return;
            _paneWorkspaceId = null;
            _paneKind = null;
            changed = BumpRevisionLocked();
        }
        LayoutChanged?.Invoke(this, changed);
    }

    private WorkspaceLayoutSnapshot BumpRevisionLocked()
    {
        _revision++;
        return CreateSnapshotLocked();
    }

    private WorkspaceLayoutSnapshot CreateSnapshotLocked() =>
        new(
            _revision,
            new[]
            {
                new WorkspacePaneSnapshot(SinglePaneId, _paneWorkspaceId, _paneKind, 1.0)
            },
            SinglePaneId);
}
