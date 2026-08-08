using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

internal enum AgentBridgeMessageKind
{
    Submit,
    AttachmentUpload,
    Command
}

internal sealed record AgentBridgeMessage(
    AgentBridgeMessageKind Kind,
    AgentSubmitEventArgs? Submit = null,
    AgentAttachmentUploadEventArgs? AttachmentUpload = null,
    AgentCommandEventArgs? Command = null);

internal static class AgentBridgeMessageParser
{
    public static bool TryParse(string? json, out AgentBridgeMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = GetString(root, "type");
            if (!Guid.TryParse(GetString(root, "workspaceId"), out var workspaceId)
                || workspaceId == Guid.Empty)
            {
                return false;
            }
            switch (type)
            {
                case "agent_submit":
                    var text = GetString(root, "text");
                    var attachments = GetStringArray(root, "attachments");
                    if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
                        return false;
                    message = new AgentBridgeMessage(
                        AgentBridgeMessageKind.Submit,
                        Submit: new AgentSubmitEventArgs
                        {
                            WorkspaceId = workspaceId,
                            Text = text,
                            AttachmentIds = attachments
                        });
                    return true;

                case "agent_upload_attachment":
                    message = new AgentBridgeMessage(
                        AgentBridgeMessageKind.AttachmentUpload,
                        AttachmentUpload: new AgentAttachmentUploadEventArgs
                        {
                            WorkspaceId = workspaceId,
                            ClientId = GetString(root, "clientId"),
                            FileName = GetString(root, "fileName"),
                            MimeType = GetString(root, "mimeType"),
                            Size = GetLong(root, "size"),
                            DataBase64 = GetString(root, "dataBase64")
                        });
                    return true;

                case "agent_command":
                case "agent_permission_response":
                case "agent_question_response":
                case "agent_elicitation_response":
                    message = new AgentBridgeMessage(
                        AgentBridgeMessageKind.Command,
                        Command: new AgentCommandEventArgs
                        {
                            WorkspaceId = workspaceId,
                            Command = type == "agent_command" ? GetString(root, "command") : type,
                            RequestId = GetString(root, "requestId"),
                            Value = GetString(root, "value"),
                            BooleanValue = GetBoolean(root, "value")
                        });
                    return true;
            }
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long GetLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool? GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }
}

internal enum TerminalBridgeMessageKind
{
    Input,
    Resize,
    Title,
    PasteRequest,
    Ready,
    PaneFocus,
    PaneRatiosCommit,
    PaneMove,
    WorkspaceLayoutIntent,
    WorkspaceCreate,
    ThemeAction
}

internal sealed record TerminalBridgeMessage(
    TerminalBridgeMessageKind Kind,
    TerminalInputEventArgs? Input = null,
    TerminalResizeEventArgs? Resize = null,
    TerminalTitleEventArgs? Title = null,
    TerminalPasteRequest? PasteRequest = null,
    string? PaneId = null,
    PaneRatiosEventArgs? PaneRatios = null,
    Guid? WorkspaceId = null,
    WorkspaceLayoutIntentEventArgs? WorkspaceIntent = null,
    WorkspaceCreateEventArgs? WorkspaceCreate = null,
    ThemeActionEventArgs? ThemeAction = null);

internal sealed record TerminalPasteRequest(Guid SessionId, Guid RequestId);

internal static class TerminalBridgeMessageParser
{
    public static bool TryParse(string? json, out TerminalBridgeMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        TerminalMessage? source;
        try
        {
            source = JsonSerializer.Deserialize<TerminalMessage>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (source == null)
            return false;

        switch (source.Type)
        {
            case "input" when source.Data != null && Guid.TryParse(source.SessionId, out var inputId):
                try
                {
                    message = new TerminalBridgeMessage(
                        TerminalBridgeMessageKind.Input,
                        Input: new TerminalInputEventArgs
                        {
                            SessionId = inputId,
                            Data = Convert.FromBase64String(source.Data)
                        });
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }

            case "resize" when Guid.TryParse(source.SessionId, out var resizeId)
                               && source.Cols.HasValue && source.Rows.HasValue:
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.Resize,
                    Resize: new TerminalResizeEventArgs
                    {
                        SessionId = resizeId,
                        Cols = source.Cols.Value,
                        Rows = source.Rows.Value
                    });
                return true;

            case "title" when Guid.TryParse(source.SessionId, out var titleId):
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.Title,
                    Title: new TerminalTitleEventArgs
                    {
                        SessionId = titleId,
                        Title = source.Title ?? "Terminal"
                    });
                return true;

            case "paste_request" when Guid.TryParse(source.SessionId, out var pasteSessionId)
                                      && Guid.TryParse(source.RequestId, out var pasteRequestId):
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.PasteRequest,
                    PasteRequest: new TerminalPasteRequest(pasteSessionId, pasteRequestId));
                return true;

            case "ready":
                message = new TerminalBridgeMessage(TerminalBridgeMessageKind.Ready);
                return true;

            case "pane_focus" when !string.IsNullOrWhiteSpace(source.PaneId):
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.PaneFocus,
                    PaneId: source.PaneId);
                return true;

            case "pane_ratios_commit" when source.BaseRevision.HasValue && source.Panes is { Count: > 1 }:
                var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var pane in source.Panes)
                {
                    if (string.IsNullOrWhiteSpace(pane.PaneId)
                        || !double.IsFinite(pane.Ratio)
                        || pane.Ratio <= 0
                        || !ratios.TryAdd(pane.PaneId, pane.Ratio))
                    {
                        return false;
                    }
                }
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.PaneRatiosCommit,
                    PaneRatios: new PaneRatiosEventArgs
                    {
                        BaseRevision = source.BaseRevision.Value,
                        Ratios = ratios
                    });
                return true;

            case "pane_move" when !string.IsNullOrWhiteSpace(source.PaneId)
                                  && Guid.TryParse(source.WorkspaceId, out var moveId):
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.PaneMove,
                    PaneId: source.PaneId,
                    WorkspaceId: moveId);
                return true;

            case "workspace_layout_intent" when source.Action is
                "activate" or "close" or "split_right" or "collapse_single":
                Guid? intentWorkspaceId = Guid.TryParse(source.WorkspaceId, out var parsedIntentId)
                    ? parsedIntentId
                    : null;
                if (source.Action is "activate" or "close" or "split_right"
                    && !intentWorkspaceId.HasValue)
                    return false;
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.WorkspaceLayoutIntent,
                    WorkspaceIntent: new WorkspaceLayoutIntentEventArgs
                    {
                        Action = source.Action,
                        WorkspaceId = intentWorkspaceId,
                        PaneId = source.PaneId
                    });
                return true;

            case "workspace_create" when source.Kind is "terminal" or "agent"
                                         && source.Placement is "focused" or "new_right":
                if (source.Kind == "agent" && string.IsNullOrWhiteSpace(source.ProviderKey))
                    return false;
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.WorkspaceCreate,
                    WorkspaceCreate: new WorkspaceCreateEventArgs
                    {
                        Kind = source.Kind,
                        ProviderKey = source.ProviderKey,
                        Placement = source.Placement
                    });
                return true;

            case "theme_action" when source.Action is "preview" or "confirm" or "cancel" or "refresh" or "open_folder":
                if (source.Action is "preview" or "confirm" && string.IsNullOrWhiteSpace(source.ThemeKey))
                    return false;
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.ThemeAction,
                    ThemeAction: new ThemeActionEventArgs
                    {
                        Action = source.Action,
                        ThemeKey = source.ThemeKey
                    });
                return true;
        }

        return false;
    }
}
