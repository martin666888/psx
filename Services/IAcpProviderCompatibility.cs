using System.Text.Json;
using PSX.Models;

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

    /// <summary>May only narrow the image capability advertised by the agent.</summary>
    bool SupportsPromptImage(bool declaredSupport) => declaredSupport;

    /// <summary>May only remove modes whose behavior is unsafe for this provider.</summary>
    IReadOnlyList<AcpSessionModeDescriptor> FilterSessionModes(
        IReadOnlyList<AcpSessionModeDescriptor> declaredModes) => declaredModes;

    /// <summary>May hide an unsafe live session configuration option.</summary>
    bool SupportsSessionConfigOption(string configId) => true;
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

internal sealed class ClineAcpProviderCompatibility : IAcpProviderCompatibility
{
    // Cline 3.0.53 advertises image input, but the pinned ACP path has not
    // demonstrated end-to-end image delivery. PSX must not offer an attachment
    // control until that behavior has an authenticated compatibility fixture.
    public bool SupportsPromptImage(bool declaredSupport) => false;

    // The unauthenticated probe cannot prove that changing Plan/Act affects the
    // next model request. Keep the safe Act surface only instead of publishing
    // an unverified state transition. A later pinned version can relax this
    // policy without a provider-name branch in the session engine.
    public IReadOnlyList<AcpSessionModeDescriptor> FilterSessionModes(
        IReadOnlyList<AcpSessionModeDescriptor> declaredModes)
    {
        var act = declaredModes.FirstOrDefault(mode =>
            string.Equals(mode.Id, "act", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode.Name, "act", StringComparison.OrdinalIgnoreCase));
        return act == null ? [] : [act];
    }

    public bool SupportsSessionConfigOption(string configId)
        => !string.Equals(configId, "mode", StringComparison.OrdinalIgnoreCase);
}
