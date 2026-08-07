namespace PSX.Models;

/// <summary>One visible slot in the window-level pane layout.</summary>
public sealed record WorkspacePaneSnapshot(
    string PaneId,
    Guid? WorkspaceId,
    WorkspaceKind? Kind,
    double Ratio);

/// <summary>
/// Canonical requested layout broadcast to the WebView. Revisions increase
/// monotonically and the frontend drops late snapshots. The on-screen
/// effective presentation (temporary narrow-width collapses) never writes
/// back into this requested layout.
/// </summary>
public sealed record WorkspaceLayoutSnapshot(
    long LayoutRevision,
    IReadOnlyList<WorkspacePaneSnapshot> Panes,
    string FocusedPaneId);
