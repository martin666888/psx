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

    event EventHandler<TerminalInputEventArgs>? InputReceived;
    event EventHandler<TerminalResizeEventArgs>? ResizeRequested;
    event EventHandler<TerminalTitleEventArgs>? TitleChanged;
    event EventHandler<string>? ViewModeChanged;
    event EventHandler? FrontendReady;
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
