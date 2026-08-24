using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Read-only Claude Code user-level config for the Usage panel「配置」tab.
/// Scopes to <c>CLAUDE_CONFIG_DIR ?? ~/.claude</c>; never reads project-level
/// files. Secrets (env values, MCP auth) are stripped via
/// <see cref="AgentConfigSanitizer"/>.
/// </summary>
public sealed class ClaudeConfigSource : IAgentConfigSource
{
    private readonly Func<string?> _configDirResolver;
    private readonly Func<string?> _userClaudeJsonResolver;

    public ClaudeConfigSource(
        Func<string?>? configDirResolver = null,
        Func<string?>? userClaudeJsonResolver = null)
    {
        _configDirResolver = configDirResolver ?? DefaultConfigDir;
        _userClaudeJsonResolver = userClaudeJsonResolver ?? DefaultUserClaudeJson;
    }

    public AgentProviderConfigReport Collect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var facts = new List<AgentConfigFact>();
        var models = new List<AgentConfigModelEntry>();
        var mcpServers = new List<AgentConfigMcpServer>();
        var skills = new List<AgentConfigSkill>();
        var notes = new List<string>();
        var state = AgentProviderConfigReport.Available;

        var configDir = _configDirResolver();
        if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir))
        {
            return new AgentProviderConfigReport(
                string.Empty, string.Empty, string.Empty,
                AgentProviderConfigReport.Unavailable,
                [], [], [], [],
                [AgentConfigNotes.HomeMissing]);
        }

        var settingsPath = Path.Combine(configDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            if (!AgentConfigSanitizer.TryReadBoundedText(settingsPath, out var settingsText, out var oversized))
            {
                if (oversized)
                {
                    state = AgentProviderConfigReport.Partial;
                    notes.Add(AgentConfigNotes.FileTooLarge);
                }
                else
                {
                    state = AgentProviderConfigReport.Partial;
                    notes.Add(AgentConfigNotes.ParseFailed);
                }
            }
            else if (!TryParseSettings(settingsText, facts, notes, ref state))
            {
                state = AgentProviderConfigReport.Partial;
                if (!notes.Contains(AgentConfigNotes.ParseFailed))
                    notes.Add(AgentConfigNotes.ParseFailed);
            }
        }

        var localPath = Path.Combine(configDir, "settings.local.json");
        if (File.Exists(localPath))
        {
            if (!AgentConfigSanitizer.TryReadBoundedText(localPath, out var localText, out var oversized))
            {
                state = AgentProviderConfigReport.Partial;
                notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
            }
            else
            {
                TryParseLocalPermissions(localText, facts, notes, ref state);
            }
        }

        var claudeJsonPath = _userClaudeJsonResolver();
        if (!string.IsNullOrWhiteSpace(claudeJsonPath) && File.Exists(claudeJsonPath))
        {
            if (!AgentConfigSanitizer.TryReadBoundedText(claudeJsonPath, out var jsonText, out var oversized))
            {
                state = AgentProviderConfigReport.Partial;
                notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
            }
            else
            {
                TryParseClaudeJsonMcp(jsonText, mcpServers, notes, ref state);
            }
        }

        foreach (var name in AgentConfigSanitizer.ListChildDirectoryNames(
                     Path.Combine(configDir, "skills")))
        {
            skills.Add(new AgentConfigSkill(name));
        }

        return new AgentProviderConfigReport(
            string.Empty, string.Empty, string.Empty,
            state,
            facts, models, mcpServers, skills, DedupNotes(notes));
    }

    private static bool TryParseSettings(
        string text,
        List<AgentConfigFact> facts,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("env", out var env)
                && env.ValueKind == JsonValueKind.Object)
            {
                var keys = AgentConfigSanitizer.CollectObjectKeys(env);
                if (keys.Count > 0)
                    facts.Add(new AgentConfigFact(AgentConfigFactLabels.EnvVarKeys, string.Join(", ", keys)));
            }

            if (root.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(model.GetString()))
            {
                facts.Add(new AgentConfigFact(AgentConfigFactLabels.DefaultModel, model.GetString()!));
            }

            if (root.TryGetProperty("enabledPlugins", out var plugins)
                && plugins.ValueKind == JsonValueKind.Object)
            {
                facts.Add(new AgentConfigFact(AgentConfigFactLabels.EnabledPlugins, plugins.EnumerateObject().Count().ToString()));
            }
            else if (root.TryGetProperty("plugins", out var pluginsAlt))
            {
                var count = pluginsAlt.ValueKind switch
                {
                    JsonValueKind.Object => pluginsAlt.EnumerateObject().Count(),
                    JsonValueKind.Array => pluginsAlt.GetArrayLength(),
                    _ => 0
                };
                if (count > 0)
                    facts.Add(new AgentConfigFact(AgentConfigFactLabels.PluginCount, count.ToString()));
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeConfigSource settings parse: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
            return false;
        }
    }

    private static void TryParseLocalPermissions(
        string text,
        List<AgentConfigFact> facts,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("permissions", out var permissions))
                return;
            if (!permissions.TryGetProperty("allow", out var allow)
                || allow.ValueKind != JsonValueKind.Array)
                return;

            var entries = allow.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (entries.Length == 0)
                return;

            const int maxChars = 400;
            var joined = string.Join(", ", entries);
            if (joined.Length > maxChars)
                joined = joined[..maxChars] + "…";
            facts.Add(new AgentConfigFact(AgentConfigFactLabels.LocalPermissionsAllow, joined));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeConfigSource local permissions: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
        }
    }

    private static void TryParseClaudeJsonMcp(
        string text,
        List<AgentConfigMcpServer> mcpServers,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
                return;

            foreach (var property in servers.EnumerateObject())
            {
                mcpServers.Add(ParseMcpServer(property.Name, property.Value));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeConfigSource ~/.claude.json mcp: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
        }
    }

    internal static AgentConfigMcpServer ParseMcpServer(string name, JsonElement value)
    {
        var enabled = true;
        if (value.TryGetProperty("disabled", out var disabled)
            && disabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = !disabled.GetBoolean();
        }

        var envKeys = value.TryGetProperty("env", out var env)
            ? AgentConfigSanitizer.CollectObjectKeys(env)
            : Array.Empty<string>();
        var headerKeys = value.TryGetProperty("headers", out var headers)
            ? AgentConfigSanitizer.CollectObjectKeys(headers)
            : Array.Empty<string>();

        if (value.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String)
        {
            var transport = AgentConfigMcpServer.TransportHttp;
            if (value.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "sse", StringComparison.OrdinalIgnoreCase))
            {
                transport = AgentConfigMcpServer.TransportSse;
            }

            return new AgentConfigMcpServer(
                name,
                transport,
                AgentConfigSanitizer.SanitizeUrl(url.GetString()),
                enabled,
                envKeys,
                headerKeys);
        }

        var command = value.TryGetProperty("command", out var cmd)
            && cmd.ValueKind == JsonValueKind.String
                ? cmd.GetString()
                : null;
        IEnumerable<string>? args = null;
        if (value.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            args = argsEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => s != null);
        }

        return new AgentConfigMcpServer(
            name,
            AgentConfigMcpServer.TransportStdio,
            AgentConfigSanitizer.SanitizeStdioTarget(command, args),
            enabled,
            envKeys,
            headerKeys);
    }

    private static IReadOnlyList<string> DedupNotes(List<string> notes)
        => notes.Distinct(StringComparer.Ordinal).ToArray();

    private static string? DefaultConfigDir()
    {
        var overridden = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude");
    }

    private static string? DefaultUserClaudeJson()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude.json");
    }
}
