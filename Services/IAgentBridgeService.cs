using Microsoft.Web.WebView2.Wpf;

namespace PSX.Services;

public interface IAgentBridgeService
{
    Task InitializeAsync(WebView2 webView);
    Task SendEventAsync(object message);

    event EventHandler<AgentSubmitEventArgs>? UserMessageSubmitted;
    event EventHandler<AgentCommandEventArgs>? CommandReceived;
    event EventHandler<AgentAttachmentUploadEventArgs>? AttachmentUploadReceived;
}

public sealed class AgentSubmitEventArgs : EventArgs
{
    public string Text { get; set; } = "";
    public List<string> AttachmentIds { get; set; } = new();
}

public sealed class AgentCommandEventArgs : EventArgs
{
    public string Command { get; set; } = "";
    public string? RequestId { get; set; }
    public string? Value { get; set; }
}

public sealed class AgentAttachmentUploadEventArgs : EventArgs
{
    public string ClientId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string DataBase64 { get; set; } = "";
}
