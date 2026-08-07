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
    event EventHandler<ThemeActionEventArgs>? ThemeActionRequested;
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
