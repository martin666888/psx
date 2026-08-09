using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PSX.Services;

public sealed class AgentBridgeService : IAgentBridgeService, IDisposable
{
    private WebView2? _webView;
    private CoreWebView2? _coreWebView;
    private WebViewJsonDispatcher? _messageDispatcher;
    private bool _disposed;

    public event EventHandler<AgentSubmitEventArgs>? UserMessageSubmitted;
    public event EventHandler<AgentCommandEventArgs>? CommandReceived;
    public event EventHandler<AgentAttachmentUploadEventArgs>? AttachmentUploadReceived;

    public Task InitializeAsync(WebView2 webView)
    {
        _webView = webView;
        _coreWebView = webView.CoreWebView2;

        if (_coreWebView != null)
        {
            _messageDispatcher?.Dispose();
            _messageDispatcher = new WebViewJsonDispatcher(
                webView.Dispatcher,
                _coreWebView.PostWebMessageAsJson);
            _coreWebView.WebMessageReceived += OnWebMessageReceived;
        }

        return Task.CompletedTask;
    }

    public Task SendEventAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        return _messageDispatcher?.SendAsync(json) ?? Task.CompletedTask;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        if (!AgentBridgeMessageParser.TryParse(json, out var message) || message == null)
            return;

        switch (message.Kind)
        {
            case AgentBridgeMessageKind.Submit:
                UserMessageSubmitted?.Invoke(this, message.Submit!);
                break;
            case AgentBridgeMessageKind.AttachmentUpload:
                AttachmentUploadReceived?.Invoke(this, message.AttachmentUpload!);
                break;
            case AgentBridgeMessageKind.Command:
                CommandReceived?.Invoke(this, message.Command!);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _messageDispatcher?.Dispose();
        _messageDispatcher = null;

        if (_coreWebView != null)
        {
            _coreWebView.WebMessageReceived -= OnWebMessageReceived;
        }

        UserMessageSubmitted = null;
        CommandReceived = null;
        AttachmentUploadReceived = null;
        _coreWebView = null;
        _webView = null;
    }
}
