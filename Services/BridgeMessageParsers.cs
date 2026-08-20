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
        if (json.Length > BridgeProtocolLimits.AgentWireEnvelopeCharacters)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = GetString(root, "type");
            switch (type)
            {
                case "agent_global_command":
                    var command = GetString(root, "command");
                    if (string.IsNullOrWhiteSpace(command))
                        return false;
                    message = new AgentBridgeMessage(
                        AgentBridgeMessageKind.Command,
                        Command: new AgentCommandEventArgs
                        {
                            WorkspaceId = Guid.Empty,
                            Command = command,
                            RequestId = GetString(root, "requestId"),
                            Value = GetString(root, "value"),
                            BooleanValue = GetBoolean(root, "value")
                        });
                    return true;
            }
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
                    if (text.Length > BridgeProtocolLimits.AgentPromptTextCharacters
                        || attachments.Count > BridgeProtocolLimits.PromptImageCount)
                    {
                        return false;
                    }
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
                    var size = GetLong(root, "size");
                    var dataBase64 = GetString(root, "dataBase64");
                    if (size > BridgeProtocolLimits.ImageBytes
                        || BridgePayloadGuard.ExceedsBase64DecodedLimit(
                            dataBase64,
                            BridgeProtocolLimits.ImageBytes))
                    {
                        return false;
                    }
                    message = new AgentBridgeMessage(
                        AgentBridgeMessageKind.AttachmentUpload,
                        AttachmentUpload: new AgentAttachmentUploadEventArgs
                        {
                            WorkspaceId = workspaceId,
                            ClientId = GetString(root, "clientId"),
                            FileName = GetString(root, "fileName"),
                            MimeType = GetString(root, "mimeType"),
                            Size = size,
                            DataBase64 = dataBase64
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
    DshCommand,
    DshExport,
    KimiWebCommand,
    KimiWebExport,
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
    DshCommandEventArgs? DshCommand = null,
    DshExportEventArgs? DshExport = null,
    KimiWebCommandEventArgs? KimiWebCommand = null,
    KimiWebExportEventArgs? KimiWebExport = null,
    ThemeActionEventArgs? ThemeAction = null);

internal sealed record TerminalPasteRequest(Guid SessionId, Guid RequestId);

internal static class TerminalBridgeMessageParser
{
    public static bool TryParse(string? json, out TerminalBridgeMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        if (json.Length > BridgeProtocolLimits.TerminalWireEnvelopeCharacters)
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
                if (BridgePayloadGuard.ExceedsBase64DecodedLimit(
                    source.Data,
                    BridgeProtocolLimits.TerminalInputBytes))
                {
                    return false;
                }
                try
                {
                    var input = Convert.FromBase64String(source.Data);
                    if (input.Length > BridgeProtocolLimits.TerminalInputBytes)
                        return false;
                    message = new TerminalBridgeMessage(
                        TerminalBridgeMessageKind.Input,
                        Input: new TerminalInputEventArgs
                        {
                            SessionId = inputId,
                            Data = input
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

            case "workspace_create" when source.Kind is "terminal" or "agent" or "dsh_web" or "kimi_web"
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

            case "dsh_command" when source.Name is "install" or "retry" or "stop"
                                                or "check_update" or "update" or "cancel_update":
            {
                // version is meaningful only for update. Other commands may
                // carry it on the wire; it is ignored rather than rejected.
                string? version = null;
                if (source.Name == "update" && !string.IsNullOrWhiteSpace(source.Version))
                {
                    var trimmed = source.Version.Trim();
                    if (!IsSafeDshVersion(trimmed) || !DshSemanticVersion.TryParse(trimmed, out _))
                        return false;
                    version = trimmed;
                }

                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.DshCommand,
                    DshCommand: new DshCommandEventArgs { Name = source.Name, Version = version });
                return true;
            }

            case "kimi_web_command" when source.Name is "stop" or "retry":
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.KimiWebCommand,
                    KimiWebCommand: new KimiWebCommandEventArgs { Name = source.Name });
                return true;

            case "dsh_export" when !string.IsNullOrWhiteSpace(source.Url)
                                   && !string.IsNullOrWhiteSpace(source.Filename):
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.DshExport,
                    DshExport: new DshExportEventArgs { Url = source.Url!, Filename = source.Filename! });
                return true;

            case "kimi_web_export" when !string.IsNullOrWhiteSpace(source.Url):
                message = new TerminalBridgeMessage(
                    TerminalBridgeMessageKind.KimiWebExport,
                    KimiWebExport: new KimiWebExportEventArgs
                    {
                        Url = source.Url!,
                        Path = source.Path,
                        SessionId = source.SessionId
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

    /// <summary>
    /// Matches the frontend <c>safeDshVersion</c> allowlist: short printable
    /// version tokens only. SemVer precedence is enforced later by the
    /// supervisor allowlist and <c>StageUpdateAsync</c>.
    /// </summary>
    internal static bool IsSafeDshVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var text = value.Trim();
        if (text.Length is < 1 or > 64)
            return false;
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-')
                continue;
            return false;
        }
        return true;
    }
}
