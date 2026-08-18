using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Read-only Kimi Code user-level config. Reads <c>mcp.json</c> and line-scans
/// <c>config.toml</c> for a few known keys (no full TOML dependency). Never
/// expands <c>extra_skill_dirs</c> in v1.
/// </summary>
public sealed partial class KimiConfigSource : IAgentConfigSource
{
    private readonly Func<string?> _homeResolver;

    public KimiConfigSource(Func<string?>? homeResolver = null)
    {
        _homeResolver = homeResolver ?? DefaultHome;
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

        var home = _homeResolver();
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            return new AgentProviderConfigReport(
                string.Empty, string.Empty, string.Empty,
                AgentProviderConfigReport.Unavailable,
                [], [], [], [],
                [AgentConfigNotes.HomeMissing]);
        }

        var mcpPath = Path.Combine(home, "mcp.json");
        if (File.Exists(mcpPath))
        {
            if (!AgentConfigSanitizer.TryReadBoundedText(mcpPath, out var mcpText, out var oversized))
            {
                state = AgentProviderConfigReport.Partial;
                notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
            }
            else
            {
                TryParseMcpJson(mcpText, mcpServers, notes, ref state);
            }
        }

        var tomlPath = Path.Combine(home, "config.toml");
        if (File.Exists(tomlPath))
        {
            if (!AgentConfigSanitizer.TryReadBoundedText(tomlPath, out var tomlText, out var oversized))
            {
                state = AgentProviderConfigReport.Partial;
                notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
            }
            else
            {
                TryScanToml(tomlText, facts, models, notes, ref state);
            }
        }

        return new AgentProviderConfigReport(
            string.Empty, string.Empty, string.Empty,
            state,
            facts, models, mcpServers, skills, notes.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void TryParseMcpJson(
        string text,
        List<AgentConfigMcpServer> mcpServers,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var servers = root;
            if (root.TryGetProperty("mcpServers", out var nested)
                && nested.ValueKind == JsonValueKind.Object)
            {
                servers = nested;
            }
            else if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in servers.EnumerateObject())
            {
                // Skip non-server wrapper keys if the file is flat mcpServers-shaped.
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                mcpServers.Add(ClaudeConfigSource.ParseMcpServer(property.Name, property.Value));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"KimiConfigSource mcp.json: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
        }
    }

    private static void TryScanToml(
        string text,
        List<AgentConfigFact> facts,
        List<AgentConfigModelEntry> models,
        List<string> notes,
        ref string state)
    {
        try
        {
            var providerSections = new List<string>();
            string? secondaryModel = null;
            var secondaryModelPool = new List<string>();
            // TOML keys belong to the most recent [section]; track it so a
            // default_model inside [secondary_model] is never misreported as
            // the global default model.
            var currentSection = string.Empty;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (AgentConfigSanitizer.LooksLikeSecretAssignment(line))
                    continue;

                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var section = SectionRegex().Match(trimmed);
                if (section.Success)
                {
                    currentSection = section.Groups[1].Value;
                    var provider = ProviderSectionRegex().Match(trimmed);
                    if (provider.Success)
                        providerSections.Add(Unquote(provider.Groups[1].Value));
                    continue;
                }

                var kv = KeyValueRegex().Match(trimmed);
                if (!kv.Success)
                    continue;

                var key = kv.Groups[1].Success
                    ? kv.Groups[1].Value
                    : kv.Groups[2].Success ? kv.Groups[2].Value : kv.Groups[3].Value;
                var value = Unquote(kv.Groups[4].Value.Trim());
                if (currentSection.Length == 0)
                {
                    if (string.Equals(key, "default_model", StringComparison.OrdinalIgnoreCase))
                    {
                        facts.Add(new AgentConfigFact("默认模型", value));
                        if (!string.IsNullOrWhiteSpace(value))
                            models.Add(new AgentConfigModelEntry(value, null, null));
                    }
                    else if (string.Equals(key, "default_plan_mode", StringComparison.OrdinalIgnoreCase))
                    {
                        facts.Add(new AgentConfigFact("默认 Plan 模式", value));
                    }
                }
                else if (string.Equals(currentSection, "secondary_model", StringComparison.Ordinal))
                {
                    if (string.Equals(key, "default_model", StringComparison.OrdinalIgnoreCase))
                        secondaryModel = value;
                }
                else if (string.Equals(currentSection, "secondary_model.models", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        secondaryModelPool.Add(key);
                }
            }

            if (!string.IsNullOrWhiteSpace(secondaryModel))
                facts.Add(new AgentConfigFact("子 Agent 模型", secondaryModel));
            if (secondaryModelPool.Count > 0)
            {
                facts.Add(new AgentConfigFact(
                    "子 Agent 模型池",
                    string.Join(", ", secondaryModelPool.Distinct(StringComparer.OrdinalIgnoreCase))));
            }

            if (providerSections.Count > 0)
            {
                facts.Add(new AgentConfigFact(
                    "自定义 Provider",
                    string.Join(", ", providerSections.Distinct(StringComparer.OrdinalIgnoreCase))));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"KimiConfigSource config.toml: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        // Strip inline comments for unquoted values.
        var hash = value.IndexOf('#');
        return hash >= 0 ? value[..hash].Trim() : value;
    }

    private static string? DefaultHome()
    {
        var overridden = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".kimi-code");
    }

    [GeneratedRegex(@"^\[([^\]]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^\[providers\.([^\]]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderSectionRegex();

    [GeneratedRegex(@"^(?:([A-Za-z0-9_.-]+)|""([^""]+)""|'([^']+)')\s*=\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();
}
