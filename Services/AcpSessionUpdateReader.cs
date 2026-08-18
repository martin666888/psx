using System.Text.Json;

namespace PSX.Services;

internal enum AcpSessionUpdateKind
{
    Unknown,
    UserMessageChunk,
    AgentMessageChunk,
    AgentThoughtChunk,
    ToolCall,
    ToolCallUpdate,
    Plan,
    AvailableCommands,
    Usage,
    ConfigOption,
    CurrentMode,
    SessionInfo
}

/// <summary>
/// Parses the stable envelope shared by live, replay, and control-only ACP
/// session updates. It deliberately contains no session state or side effects;
/// the session service decides which projection receives the parsed update.
/// </summary>
internal static class AcpSessionUpdateReader
{
    public static AcpSessionUpdateKind ReadKind(JsonElement update)
    {
        if (!update.TryGetProperty("sessionUpdate", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return AcpSessionUpdateKind.Unknown;
        }

        return value.GetString() switch
        {
            "user_message_chunk" => AcpSessionUpdateKind.UserMessageChunk,
            "agent_message_chunk" => AcpSessionUpdateKind.AgentMessageChunk,
            "agent_thought_chunk" => AcpSessionUpdateKind.AgentThoughtChunk,
            "tool_call" => AcpSessionUpdateKind.ToolCall,
            "tool_call_update" => AcpSessionUpdateKind.ToolCallUpdate,
            "plan" => AcpSessionUpdateKind.Plan,
            "available_commands_update" => AcpSessionUpdateKind.AvailableCommands,
            "usage_update" => AcpSessionUpdateKind.Usage,
            "config_option_update" => AcpSessionUpdateKind.ConfigOption,
            "current_mode_update" => AcpSessionUpdateKind.CurrentMode,
            "session_info_update" => AcpSessionUpdateKind.SessionInfo,
            _ => AcpSessionUpdateKind.Unknown
        };
    }

    public static string ReadContentText(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "text")
        {
            return content.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? ""
                    : "";
        }

        return content.GetRawText();
    }
}
