using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Read-only scan of Cline's user-level configuration under ~/.cline. Values
/// that may contain credentials are never projected; provider identities,
/// sanitized MCP endpoints and skill directory names are sufficient for the
/// global Config panel.
/// </summary>
public sealed class ClineConfigSource : IAgentConfigSource
{
    private readonly Func<string?> _homeResolver;

    public ClineConfigSource(Func<string?>? homeResolver = null)
    {
        _homeResolver = homeResolver ?? DefaultHome;
    }

    public AgentProviderConfigReport Collect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var home = _homeResolver();
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            return Empty(AgentProviderConfigReport.Unavailable, AgentConfigNotes.HomeMissing);

        var facts = new List<AgentConfigFact>();
        var mcpServers = new List<AgentConfigMcpServer>();
        var skills = new List<AgentConfigSkill>();
        var notes = new List<string>();
        var state = AgentProviderConfigReport.Available;

        ReadProviders(
            Path.Combine(home, "data", "settings", "providers.json"),
            facts,
            notes,
            ref state);
        ReadMcp(
            Path.Combine(home, "data", "settings", "cline_mcp_settings.json"),
            mcpServers,
            notes,
            ref state);

        foreach (var directory in new[]
                 {
                     Path.Combine(home, "skills"),
                     Path.Combine(home, "data", "skills")
                 })
        {
            foreach (var name in AgentConfigSanitizer.ListChildDirectoryNames(directory))
                skills.Add(new AgentConfigSkill(name));
        }

        var ruleCount = CountImmediateFiles(Path.Combine(home, "rules"));
        if (ruleCount > 0)
            facts.Add(new AgentConfigFact("用户规则", ruleCount.ToString()));

        return new AgentProviderConfigReport(
            string.Empty,
            string.Empty,
            string.Empty,
            state,
            facts,
            [],
            mcpServers
                .GroupBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray(),
            skills
                .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray(),
            notes.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ReadProviders(
        string path,
        List<AgentConfigFact> facts,
        List<string> notes,
        ref string state)
    {
        if (!File.Exists(path))
            return;
        if (!TryRead(path, notes, ref state, out var text))
            return;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            JsonElement providers;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("providers", out var nested)
                && nested.ValueKind == JsonValueKind.Object)
            {
                providers = nested;
            }
            else
            {
                providers = root;
            }

            if (providers.ValueKind != JsonValueKind.Object)
                return;
            var ids = providers.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !LooksSecretKey(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ids.Length > 0)
                facts.Add(new AgentConfigFact("已配置 Provider", string.Join(", ", ids)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClineConfigSource providers: {ex.Message}");
            MarkPartial(notes, ref state, AgentConfigNotes.ParseFailed);
        }
    }

    private static void ReadMcp(
        string path,
        List<AgentConfigMcpServer> servers,
        List<string> notes,
        ref string state)
    {
        if (!File.Exists(path))
            return;
        if (!TryRead(path, notes, ref state, out var text))
            return;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("mcpServers", out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                servers.Add(ParseMcpServer(property.Name, property.Value));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClineConfigSource MCP: {ex.Message}");
            MarkPartial(notes, ref state, AgentConfigNotes.ParseFailed);
        }
    }

    internal static AgentConfigMcpServer ParseMcpServer(string name, JsonElement value)
    {
        var disabled = value.TryGetProperty("disabled", out var disabledElement)
            && disabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && disabledElement.GetBoolean();
        var envKeys = value.TryGetProperty("env", out var env)
            ? AgentConfigSanitizer.CollectObjectKeys(env)
            : [];
        var headerKeys = value.TryGetProperty("headers", out var headers)
            ? AgentConfigSanitizer.CollectObjectKeys(headers)
            : [];

        if (value.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String)
        {
            var raw = url.GetString();
            var configuredType = value.TryGetProperty("type", out var typeElement)
                                 && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            var transport = string.Equals(configuredType, "sse", StringComparison.OrdinalIgnoreCase)
                ? AgentConfigMcpServer.TransportSse
                : AgentConfigMcpServer.TransportHttp;
            return new AgentConfigMcpServer(
                name,
                transport,
                AgentConfigSanitizer.SanitizeUrl(raw),
                !disabled,
                envKeys,
                headerKeys);
        }

        var command = value.TryGetProperty("command", out var commandElement)
                      && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString()
            : null;
        var args = value.TryGetProperty("args", out var argsElement)
                   && argsElement.ValueKind == JsonValueKind.Array
            ? argsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : [];
        return new AgentConfigMcpServer(
            name,
            AgentConfigMcpServer.TransportStdio,
            AgentConfigSanitizer.SanitizeStdioTarget(command, args),
            !disabled,
            envKeys,
            headerKeys);
    }

    private static bool TryRead(
        string path,
        List<string> notes,
        ref string state,
        out string text)
    {
        if (AgentConfigSanitizer.TryReadBoundedText(path, out text, out var oversized))
            return true;
        MarkPartial(notes, ref state, oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
        return false;
    }

    private static void MarkPartial(List<string> notes, ref string state, string note)
    {
        state = AgentProviderConfigReport.Partial;
        notes.Add(note);
    }

    private static bool LooksSecretKey(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("key", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal)
            || lower.Contains("secret", StringComparison.Ordinal)
            || lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("auth", StringComparison.Ordinal);
    }

    private static int CountImmediateFiles(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0; }
        catch { return 0; }
    }

    private static AgentProviderConfigReport Empty(string state, string note)
        => new(string.Empty, string.Empty, string.Empty, state, [], [], [], [], [note]);

    private static string? DefaultHome()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".cline");
    }
}
