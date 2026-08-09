using System.Text.Json;

namespace PSX.Services;

/// <summary>
/// Isolates optional provider wire extensions from the shared ACP session
/// engine. Implementations may recognize legacy frontend aliases or enrich a
/// standard ACP tool title from provider-owned metadata.
/// </summary>
public interface IAcpProviderCompatibility
{
    bool IsLegacyPromptCommand(string command) => false;

    string ResolveToolName(JsonElement update)
        => AcpToolStateTracker.ReadStandardName(update);
}

internal sealed class DefaultAcpProviderCompatibility : IAcpProviderCompatibility
{
    public static DefaultAcpProviderCompatibility Instance { get; } = new();

    private DefaultAcpProviderCompatibility() { }
}

internal sealed class ClaudeAcpProviderCompatibility : IAcpProviderCompatibility
{
    public bool IsLegacyPromptCommand(string command)
        => string.Equals(command, "claude_command", StringComparison.Ordinal);

    public string ResolveToolName(JsonElement update)
    {
        if (TryReadNestedString(update, out var toolName, "_meta", "claudeCode", "toolName")
            && !string.IsNullOrWhiteSpace(toolName))
        {
            return toolName;
        }

        return AcpToolStateTracker.ReadStandardName(update);
    }

    private static bool TryReadNestedString(
        JsonElement element,
        out string value,
        params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                value = "";
                return false;
            }
        }

        value = current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
        return current.ValueKind == JsonValueKind.String;
    }
}
