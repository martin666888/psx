using PSX.Models;

namespace PSX.Services;

public interface ITabManagementService
{
    Task<Guid> CreateTabAsync(ShellProfile? profile = null);
    Task CloseTabAsync(Guid sessionId);
    Task SwitchTabAsync(Guid sessionId);
    Task ResizeTabAsync(Guid sessionId, int cols, int rows);
    TerminalSession? GetSession(Guid sessionId);
    /// <summary>Await ConPTY session teardown before app exit.</summary>
    Task ShutdownAsync(TimeSpan? timeout = null);

    event EventHandler<TabCreatedEventArgs>? TabCreated;
    event EventHandler<TabClosedEventArgs>? TabClosed;
    event EventHandler<TabTitleChangedEventArgs>? TabTitleChanged;
    /// <summary>Click-to-focus intent for a pane (split panes), forwarded
    /// from the neutral bridge.</summary>
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
    event EventHandler<DshExportEventArgs>? DshExportRequested;
    /// <summary>Kimi Web session export from the embedded Kimi Web tab
    /// (forwarded from the neutral bridge).</summary>
    event EventHandler<KimiWebExportEventArgs>? KimiWebExportRequested;
}

public class TabCreatedEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public string Title { get; set; } = "Terminal";
}

public class TabClosedEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
}

public class TabTitleChangedEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public string Title { get; set; } = "";
}
