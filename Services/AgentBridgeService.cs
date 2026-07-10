using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PSX.Services;

public sealed class AgentBridgeService : IAgentBridgeService, IDisposable
{
    private WebView2? _webView;
    private CoreWebView2? _coreWebView;
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
            _coreWebView.WebMessageReceived += OnWebMessageReceived;
        }

        return Task.CompletedTask;
    }

    public Task SendEventAsync(object message)
    {
        if (_coreWebView == null)
            return Task.CompletedTask;

        var json = JsonSerializer.Serialize(message);
        var coreWebView = _coreWebView;
        var dispatcher = _webView?.Dispatcher;

        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => coreWebView.PostWebMessageAsJson(json));
        }
        else
        {
            coreWebView.PostWebMessageAsJson(json);
        }

        return Task.CompletedTask;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = GetString(root, "type");

            switch (type)
            {
                case "agent_submit":
                    var text = GetString(root, "text");
                    var attachments = GetStringArray(root, "attachments");
                    if (!string.IsNullOrWhiteSpace(text) || attachments.Count > 0)
                    {
                        UserMessageSubmitted?.Invoke(this, new AgentSubmitEventArgs
                        {
                            Text = text,
                            AttachmentIds = attachments
                        });
                    }
                    break;

                case "agent_upload_attachment":
                    AttachmentUploadReceived?.Invoke(this, new AgentAttachmentUploadEventArgs
                    {
                        ClientId = GetString(root, "clientId"),
                        FileName = GetString(root, "fileName"),
                        MimeType = GetString(root, "mimeType"),
                        Size = GetLong(root, "size"),
                        DataBase64 = GetString(root, "dataBase64")
                    });
                    break;

                case "agent_command":
                case "agent_permission_response":
                case "agent_question_response":
                case "agent_elicitation_response":
                    CommandReceived?.Invoke(this, new AgentCommandEventArgs
                    {
                        Command = type == "agent_command" ? GetString(root, "command") : type,
                        RequestId = GetString(root, "requestId"),
                        Value = GetString(root, "value")
                    });
                    break;
            }
        }
        catch
        {
            // Ignore malformed frontend messages.
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : 0;
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<string>();

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

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
