using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Wpf;

namespace PSX.Services;

/// <summary>
/// Isolates one Agent Workspace from the application-wide WebView bridge.
/// Incoming events are raised only by the coordinator, and every outgoing
/// event is stamped with the Workspace ID here so session code cannot omit it.
/// </summary>
public sealed class AgentWorkspaceEventSink : IAgentBridgeService, IDisposable
{
    private readonly IAgentBridgeService _rootBridge;
    private readonly Func<JsonObject, bool>? _beforeSend;
    private bool _disposed;

    public AgentWorkspaceEventSink(
        Guid workspaceId,
        IAgentBridgeService rootBridge,
        Func<JsonObject, bool>? beforeSend = null)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Agent Workspace ID must not be empty.", nameof(workspaceId));

        WorkspaceId = workspaceId;
        _rootBridge = rootBridge;
        _beforeSend = beforeSend;
    }

    public Guid WorkspaceId { get; }

    public event EventHandler<AgentSubmitEventArgs>? UserMessageSubmitted;
    public event EventHandler<AgentCommandEventArgs>? CommandReceived;
    public event EventHandler<AgentAttachmentUploadEventArgs>? AttachmentUploadReceived;

    public Task InitializeAsync(WebView2 webView) => Task.CompletedTask;

    public Task SendEventAsync(object message)
    {
        if (_disposed)
            return Task.CompletedTask;

        var node = JsonSerializer.SerializeToNode(message) as JsonObject ?? new JsonObject();
        node["workspaceId"] = WorkspaceId.ToString();
        if (_beforeSend != null && !_beforeSend(node))
            return Task.CompletedTask;

        return _rootBridge.SendEventAsync(node);
    }

    public void Submit(AgentSubmitEventArgs args)
    {
        if (!_disposed)
            UserMessageSubmitted?.Invoke(this, args);
    }

    public void Command(AgentCommandEventArgs args)
    {
        if (!_disposed)
            CommandReceived?.Invoke(this, args);
    }

    public void Upload(AgentAttachmentUploadEventArgs args)
    {
        if (!_disposed)
            AttachmentUploadReceived?.Invoke(this, args);
    }

    public void Dispose()
    {
        _disposed = true;
        UserMessageSubmitted = null;
        CommandReceived = null;
        AttachmentUploadReceived = null;
    }
}
