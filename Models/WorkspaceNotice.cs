namespace PSX.Models;

/// <summary>
/// Fixed <c>workspace_notice</c> codes. The notice payload carries only the
/// code (plus optional safe args); the frontend maps each code to display
/// copy, so composed sentences never cross the bridge.
/// </summary>
public static class WorkspaceNoticeCode
{
    public const string WorktreeConflict = "workspace.worktree_conflict";

    public const string DshExportInvalidResponse = "dsh.export.invalid_response";
    public const string DshExportInvalidContentType = "dsh.export.invalid_content_type";
    public const string DshExportInvalidData = "dsh.export.invalid_data";
    public const string DshExportTooLarge = "dsh.export.too_large";
    public const string DshExportWriteFailed = "dsh.export.write_failed";
    public const string DshExportConnectFailed = "dsh.export.connect_failed";

    public const string KimiExportNotReady = "kimi.export.not_ready";
    public const string KimiExportInvalidRequest = "kimi.export.invalid_request";
    public const string KimiExportInvalidResponse = "kimi.export.invalid_response";
    public const string KimiExportInvalidContentType = "kimi.export.invalid_content_type";
    public const string KimiExportInvalidData = "kimi.export.invalid_data";
    public const string KimiExportTooLarge = "kimi.export.too_large";
    public const string KimiExportWriteFailed = "kimi.export.write_failed";
    public const string KimiExportConnectFailed = "kimi.export.connect_failed";
}
