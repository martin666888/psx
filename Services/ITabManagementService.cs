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
