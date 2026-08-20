using PSX.Models;

namespace PSX.Services;

public interface ITerminalBridgeService
{
    Task InitializeAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView);
    Task CreateTerminalAsync(Guid sessionId);
    Task SendOutputAsync(Guid sessionId, string base64Data);
    Task SwitchTerminalAsync(Guid sessionId);
    Task CloseTerminalAsync(Guid sessionId);
    Task ResizeTerminalAsync(Guid sessionId, int cols, int rows);
    Task SetViewModeAsync(string mode);
    Task SendAppearanceAsync(AppearanceSettings appearance);
    /// <summary>Set (or clear) the frame-origin slot for one embedded web
    /// runtime ("dsh" / "kimi_web"). A null origin clears the slot; the
    /// frame navigation whitelist matches any slot's current origin.</summary>
    void SetFrameOrigin(string kind, string? origin);
    /// <summary>Prepare a runtime's document-start frame script before its
    /// origin and Ready URL are published. Kimi Web awaits this handshake so
    /// its first navigation cannot race export mediation.</summary>
    Task PrepareFrameOriginAsync(string kind, string origin);
    /// <summary>Compatibility wrapper for the DSH slot (SetFrameOrigin with
    /// kind "dsh").</summary>
    void SetDshOrigin(string? origin);

    event EventHandler<TerminalInputEventArgs>? InputReceived;
    event EventHandler<TerminalResizeEventArgs>? ResizeRequested;
    event EventHandler<TerminalTitleEventArgs>? TitleChanged;
    event EventHandler<string>? ViewModeChanged;
    event EventHandler? FrontendReady;
    /// <summary>Click-to-focus intent for a pane (split panes).</summary>
    event EventHandler<string>? PaneFocusRequested;
    /// <summary>Divider drag end: one atomic ratio vector for the layout revision.</summary>
    event EventHandler<PaneRatiosEventArgs>? PaneRatiosRequested;
    /// <summary>Drag a workspace onto a pane (swap/replace).</summary>
    event EventHandler<PaneMoveEventArgs>? PaneMoveRequested;
    event EventHandler<WorkspaceLayoutIntentEventArgs>? WorkspaceLayoutIntentRequested;
    event EventHandler<WorkspaceCreateEventArgs>? WorkspaceCreateRequested;
    event EventHandler<DshCommandEventArgs>? DshCommandRequested;
    /// <summary>kimi_web_command (stop | retry) from the embedded Kimi Web tab.</summary>
    event EventHandler<KimiWebCommandEventArgs>? KimiWebCommandRequested;
    /// <summary>DSH session-log export: the export URL DSH built (host validates
    /// its origin against the current ready URL and locks the path to
    /// /api/session.export) plus a suggested archive filename.</summary>
    event EventHandler<DshExportEventArgs>? DshExportRequested;
    /// <summary>Kimi Web session export from the embedded Kimi Web tab: the
    /// absolute export URL the page built (host validates its origin against
    /// the current kimi origin and locks the path to
    /// /api/v1/sessions/{id}/export) plus the path/sessionId it carries. The
    /// frontend never sends a token — the Authorization header comes only
    /// from the supervisor's in-memory token.</summary>
    event EventHandler<KimiWebExportEventArgs>? KimiWebExportRequested;
    event EventHandler<ThemeActionEventArgs>? ThemeActionRequested;
}

public sealed class DshCommandEventArgs : EventArgs
{
    public required string Name { get; init; }
    /// <summary>Exact package version for <c>update</c> only; ignored on other commands.</summary>
    public string? Version { get; init; }
}

public sealed class KimiWebCommandEventArgs : EventArgs
{
    public required string Name { get; init; }
}

public sealed class DshExportEventArgs : EventArgs
{
    /// <summary>Absolute export URL DSH built (carries sessionId as a query param).</summary>
    public required string Url { get; init; }
    /// <summary>Suggested archive filename from the export anchor's download attribute.</summary>
    public required string Filename { get; init; }
}

public sealed class KimiWebExportEventArgs : EventArgs
{
    /// <summary>Absolute export URL the page built. The host validates its
    /// origin against the current kimi origin and locks the path to
    /// /api/v1/sessions/{id}/export before fetching; never trusted blindly.</summary>
    public required string Url { get; init; }
    /// <summary>Pathname portion of the export URL, informational (never used
    /// to pick the fetch target).</summary>
    public string? Path { get; init; }
    /// <summary>Session id embedded in the path, informational (used only for
    /// a suggested archive filename).</summary>
    public string? SessionId { get; init; }
}

public sealed class PaneRatiosEventArgs : EventArgs
{
    public long BaseRevision { get; init; }
    public required IReadOnlyDictionary<string, double> Ratios { get; init; }
}

public sealed class PaneMoveEventArgs : EventArgs
{
    public Guid WorkspaceId { get; init; }
    public required string PaneId { get; init; }
}

public sealed class WorkspaceLayoutIntentEventArgs : EventArgs
{
    public required string Action { get; init; }
    public Guid? WorkspaceId { get; init; }
    public string? PaneId { get; init; }
}

public sealed class WorkspaceCreateEventArgs : EventArgs
{
    public required string Kind { get; init; }
    public string? ProviderKey { get; init; }
    public required string Placement { get; init; }
}

public sealed class ThemeActionEventArgs : EventArgs
{
    public required string Action { get; init; }
    public string? ThemeKey { get; init; }
}

public class TerminalInputEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public class TerminalResizeEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
}

public class TerminalTitleEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public string Title { get; set; } = "";
}
