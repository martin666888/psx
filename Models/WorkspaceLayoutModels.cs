namespace PSX.Models;

/// <summary>One tab inside a layout column: a workspace identity plus kind.</summary>
public sealed record WorkspaceTabSnapshot(Guid WorkspaceId, WorkspaceKind Kind);

/// <summary>One ordered column (editor group) in the window-level layout:
/// its tab stack, the active tab and its width share.</summary>
public sealed record WorkspaceColumnSnapshot(
    string ColumnId,
    IReadOnlyList<WorkspaceTabSnapshot> Tabs,
    Guid? ActiveTabId,
    double Ratio);

/// <summary>
/// Canonical requested layout broadcast to the WebView. Revisions increase
/// monotonically and the frontend drops late snapshots. Every open workspace
/// is exactly one tab in exactly one column (there is no "background"
/// workspace set); the WebView owns the effective pixel presentation and
/// never writes back into this requested layout.
/// </summary>
public sealed record WorkspaceLayoutSnapshot(
    long LayoutRevision,
    IReadOnlyList<WorkspaceColumnSnapshot> Columns,
    string FocusedColumnId);
