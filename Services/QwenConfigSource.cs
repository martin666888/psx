using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Read-only Qwen Code user-level config under <c>~/.qwen</c> (or an official
/// <c>QWEN_*</c> home override when present).
/// </summary>
public sealed class QwenConfigSource : IAgentConfigSource
{
    private readonly Func<string?> _homeResolver;

    public QwenConfigSource(Func<string?>? homeResolver = null)
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

        var settingsPath = Path.Combine(home, "settings.json");
        if (File.Exists(settingsPath))
        {
            if (!AgentConfigSanitizer.TryReadBoundedText(settingsPath, out var text, out var oversized))
            {
                state = AgentProviderConfigReport.Partial;
                notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
            }
            else
            {
                TryParseSettings(text, facts, models, mcpServers, notes, ref state);
            }
        }

        foreach (var name in AgentConfigSanitizer.ListChildDirectoryNames(
                     Path.Combine(home, "skills")))
        {
            skills.Add(new AgentConfigSkill(name));
        }

        return new AgentProviderConfigReport(
            string.Empty, string.Empty, string.Empty,
            state,
            facts, models, mcpServers, skills, notes.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void TryParseSettings(
        string text,
        List<AgentConfigFact> facts,
        List<AgentConfigModelEntry> models,
        List<AgentConfigMcpServer> mcpServers,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var model))
            {
                if (model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    var modelName = name.GetString()!;
                    facts.Add(new AgentConfigFact("默认模型", modelName));
                    models.Add(new AgentConfigModelEntry(modelName, modelName, null));
                }
                else if (model.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(model.GetString()))
                {
                    var modelName = model.GetString()!;
                    facts.Add(new AgentConfigFact("默认模型", modelName));
                    models.Add(new AgentConfigModelEntry(modelName, modelName, null));
                }
            }

            if (root.TryGetProperty("modelProviders", out var providers)
                && providers.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in providers.EnumerateObject())
                {
                    string? baseUrl = null;
                    string? display = property.Name;
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (property.Value.TryGetProperty("baseUrl", out var bu)
                            && bu.ValueKind == JsonValueKind.String)
                        {
                            baseUrl = AgentConfigSanitizer.SanitizeUrl(bu.GetString());
                        }
                        else if (property.Value.TryGetProperty("base_url", out var bu2)
                                 && bu2.ValueKind == JsonValueKind.String)
                        {
                            baseUrl = AgentConfigSanitizer.SanitizeUrl(bu2.GetString());
                        }

                        if (property.Value.TryGetProperty("name", out var n)
                            && n.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(n.GetString()))
                        {
                            display = n.GetString()!;
                        }
                    }

                    models.Add(new AgentConfigModelEntry(property.Name, display, baseUrl));
                }
            }

            if (root.TryGetProperty("mcpServers", out var servers)
                && servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in servers.EnumerateObject())
                    mcpServers.Add(ParseQwenMcp(property.Name, property.Value));
            }

            if (root.TryGetProperty("env", out var env)
                && env.ValueKind == JsonValueKind.Object)
            {
                var keys = AgentConfigSanitizer.CollectObjectKeys(env);
                if (keys.Count > 0)
                    facts.Add(new AgentConfigFact("环境变量键", string.Join(", ", keys)));
            }

            if (root.TryGetProperty("security", out var security)
                && security.ValueKind == JsonValueKind.Object
                && security.TryGetProperty("auth", out var auth)
                && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("selectedType", out var selected)
                && selected.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(selected.GetString()))
            {
                facts.Add(new AgentConfigFact("认证类型", selected.GetString()!));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"QwenConfigSource settings: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
        }
    }

    private static AgentConfigMcpServer ParseQwenMcp(string name, JsonElement value)
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

        if (value.TryGetProperty("httpUrl", out var httpUrl)
            && httpUrl.ValueKind == JsonValueKind.String)
        {
            return new AgentConfigMcpServer(
                name,
                AgentConfigMcpServer.TransportHttp,
                AgentConfigSanitizer.SanitizeUrl(httpUrl.GetString()),
                enabled,
                envKeys,
                headerKeys);
        }

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
                .Select(e => e.GetString()!);
        }

        return new AgentConfigMcpServer(
            name,
            AgentConfigMcpServer.TransportStdio,
            AgentConfigSanitizer.SanitizeStdioTarget(command, args),
            enabled,
            envKeys,
            headerKeys);
    }

    private static string? DefaultHome()
    {
        // Prefer any official QWEN_* home override if present; otherwise ~/.qwen.
        foreach (var key in new[] { "QWEN_CODE_HOME", "QWEN_HOME", "QWEN_CONFIG_DIR" })
        {
            var overridden = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".qwen");
    }
}
