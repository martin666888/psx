using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Read-only OpenCode user-level config. Prefers <c>OPENCODE_CONFIG</c>, else
/// <c>~/.config/opencode/opencode.json(c)</c>. Auth file is enumerated for
/// provider ids only — values are never read into the DTO.
/// </summary>
public sealed class OpencodeConfigSource : IAgentConfigSource
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Func<string?> _configFileResolver;
    private readonly Func<string?> _configDirResolver;
    private readonly Func<string?> _authRootResolver;

    public OpencodeConfigSource(
        Func<string?>? configFileResolver = null,
        Func<string?>? configDirResolver = null,
        Func<string?>? authRootResolver = null)
    {
        _configFileResolver = configFileResolver ?? DefaultConfigFile;
        _configDirResolver = configDirResolver ?? DefaultConfigDir;
        _authRootResolver = authRootResolver ?? DefaultAuthRoot;
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

        var configPath = _configFileResolver();
        var configDir = _configDirResolver();

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            // No config file is still a valid empty state when the config dir
            // exists (or auth can be listed); otherwise mark unavailable.
            if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir))
            {
                var authRootProbe = _authRootResolver();
                if (string.IsNullOrWhiteSpace(authRootProbe) || !Directory.Exists(authRootProbe))
                {
                    return new AgentProviderConfigReport(
                        string.Empty, string.Empty, string.Empty,
                        AgentProviderConfigReport.Unavailable,
                        [], [], [], [],
                        [AgentConfigNotes.HomeMissing]);
                }
            }
        }
        else if (!AgentConfigSanitizer.TryReadBoundedText(configPath, out var text, out var oversized))
        {
            state = AgentProviderConfigReport.Partial;
            notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
        }
        else if (!TryParseConfig(text, facts, models, mcpServers, notes, ref state))
        {
            state = AgentProviderConfigReport.Partial;
            if (!notes.Contains(AgentConfigNotes.ParseFailed))
                notes.Add(AgentConfigNotes.ParseFailed);
        }

        if (!string.IsNullOrWhiteSpace(configDir))
        {
            foreach (var name in AgentConfigSanitizer.ListChildDirectoryNames(
                         Path.Combine(configDir, "skills")))
            {
                skills.Add(new AgentConfigSkill(name));
            }
        }

        var authRoot = _authRootResolver();
        if (!string.IsNullOrWhiteSpace(authRoot))
        {
            var authPath = Path.Combine(authRoot, "auth.json");
            if (File.Exists(authPath))
            {
                if (!AgentConfigSanitizer.TryReadBoundedText(authPath, out var authText, out var oversized))
                {
                    state = AgentProviderConfigReport.Partial;
                    notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
                }
                else
                {
                    TryParseAuthIds(authText, facts, notes, ref state);
                }
            }
        }

        return new AgentProviderConfigReport(
            string.Empty, string.Empty, string.Empty,
            state,
            facts, models, mcpServers, skills,
            notes.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool TryParseConfig(
        string text,
        List<AgentConfigFact> facts,
        List<AgentConfigModelEntry> models,
        List<AgentConfigMcpServer> mcpServers,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, JsoncOptions);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(model.GetString()))
            {
                var id = model.GetString()!;
                facts.Add(new AgentConfigFact("默认模型", id));
                models.Add(new AgentConfigModelEntry(id, null, null));
            }

            if (root.TryGetProperty("small_model", out var small)
                && small.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(small.GetString()))
            {
                facts.Add(new AgentConfigFact("小模型", small.GetString()!));
            }

            // provider (v1 schema) and providers (docs variant) — keys only for
            // identity; nested model ids when present; baseURL sanitized.
            ParseProviders(root, "provider", models, facts);
            ParseProviders(root, "providers", models, facts);

            if (root.TryGetProperty("disabled_providers", out var disabled)
                && disabled.ValueKind == JsonValueKind.Array)
            {
                var ids = disabled.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                if (ids.Length > 0)
                    facts.Add(new AgentConfigFact("已禁用 Provider", string.Join(", ", ids)));
            }

            if (root.TryGetProperty("instructions", out var instructions))
            {
                if (instructions.ValueKind == JsonValueKind.Array)
                {
                    facts.Add(new AgentConfigFact(
                        "指令文件数",
                        instructions.GetArrayLength().ToString()));
                }
                else if (instructions.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(instructions.GetString()))
                {
                    facts.Add(new AgentConfigFact("指令", instructions.GetString()!));
                }
            }

            if (root.TryGetProperty("mcp", out var mcp)
                && mcp.ValueKind == JsonValueKind.Object)
            {
                // v1: mcp.<name>; v2 docs: mcp.servers.<name>
                if (mcp.TryGetProperty("servers", out var servers)
                    && servers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in servers.EnumerateObject())
                        mcpServers.Add(ParseOpencodeMcp(property.Name, property.Value));
                }
                else
                {
                    foreach (var property in mcp.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object)
                            continue;
                        mcpServers.Add(ParseOpencodeMcp(property.Name, property.Value));
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpencodeConfigSource config: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
            return false;
        }
    }

    private static void ParseProviders(
        JsonElement root,
        string propertyName,
        List<AgentConfigModelEntry> models,
        List<AgentConfigFact> facts)
    {
        if (!root.TryGetProperty(propertyName, out var providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var ids = new List<string>();
        foreach (var property in providers.EnumerateObject())
        {
            ids.Add(property.Name);
            string? baseUrl = null;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (property.Value.TryGetProperty("baseURL", out var bu)
                    && bu.ValueKind == JsonValueKind.String)
                {
                    baseUrl = AgentConfigSanitizer.SanitizeUrl(bu.GetString());
                }
                else if (property.Value.TryGetProperty("baseUrl", out var bu2)
                         && bu2.ValueKind == JsonValueKind.String)
                {
                    baseUrl = AgentConfigSanitizer.SanitizeUrl(bu2.GetString());
                }
                else if (property.Value.TryGetProperty("options", out var options)
                         && options.ValueKind == JsonValueKind.Object
                         && options.TryGetProperty("baseURL", out var bu3)
                         && bu3.ValueKind == JsonValueKind.String)
                {
                    baseUrl = AgentConfigSanitizer.SanitizeUrl(bu3.GetString());
                }

                if (property.Value.TryGetProperty("models", out var nestedModels)
                    && nestedModels.ValueKind == JsonValueKind.Object)
                {
                    foreach (var model in nestedModels.EnumerateObject())
                    {
                        string? display = model.Name;
                        if (model.Value.ValueKind == JsonValueKind.Object
                            && model.Value.TryGetProperty("name", out var n)
                            && n.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(n.GetString()))
                        {
                            display = n.GetString();
                        }

                        models.Add(new AgentConfigModelEntry(
                            $"{property.Name}/{model.Name}",
                            display,
                            baseUrl));
                    }
                    continue;
                }
            }

            models.Add(new AgentConfigModelEntry(property.Name, property.Name, baseUrl));
        }

        if (ids.Count > 0)
        {
            facts.Add(new AgentConfigFact(
                "自定义 Provider",
                string.Join(", ", ids)));
        }
    }

    internal static AgentConfigMcpServer ParseOpencodeMcp(string name, JsonElement value)
    {
        var enabled = true;
        if (value.TryGetProperty("enabled", out var enabledEl)
            && enabledEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = enabledEl.GetBoolean();
        }
        else if (value.TryGetProperty("disabled", out var disabled)
                 && disabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = !disabled.GetBoolean();
        }

        var envKeys = new List<string>();
        if (value.TryGetProperty("environment", out var environment))
            envKeys.AddRange(AgentConfigSanitizer.CollectObjectKeys(environment));
        if (value.TryGetProperty("env", out var env))
            envKeys.AddRange(AgentConfigSanitizer.CollectObjectKeys(env));

        var headerKeys = value.TryGetProperty("headers", out var headers)
            ? AgentConfigSanitizer.CollectObjectKeys(headers)
            : Array.Empty<string>();

        var type = value.TryGetProperty("type", out var typeEl)
            && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString() ?? string.Empty
                : string.Empty;

        if (string.Equals(type, "remote", StringComparison.OrdinalIgnoreCase)
            || value.TryGetProperty("url", out _))
        {
            var url = value.TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String
                    ? urlEl.GetString()
                    : null;
            return new AgentConfigMcpServer(
                name,
                AgentConfigMcpServer.TransportHttp,
                AgentConfigSanitizer.SanitizeUrl(url),
                enabled,
                envKeys.Distinct(StringComparer.Ordinal).ToArray(),
                headerKeys);
        }

        // local / stdio: command may be string or string[]
        string? command = null;
        IEnumerable<string>? args = null;
        if (value.TryGetProperty("command", out var cmdEl))
        {
            if (cmdEl.ValueKind == JsonValueKind.String)
            {
                command = cmdEl.GetString();
            }
            else if (cmdEl.ValueKind == JsonValueKind.Array)
            {
                var parts = cmdEl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                if (parts.Length > 0)
                {
                    command = parts[0];
                    args = parts.Skip(1);
                }
            }
        }

        return new AgentConfigMcpServer(
            name,
            AgentConfigMcpServer.TransportStdio,
            AgentConfigSanitizer.SanitizeStdioTarget(command, args),
            enabled,
            envKeys.Distinct(StringComparer.Ordinal).ToArray(),
            headerKeys);
    }

    private static void TryParseAuthIds(
        string text,
        List<AgentConfigFact> facts,
        List<string> notes,
        ref string state)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, JsoncOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            // Keys only — never enumerate values into the DTO.
            var ids = AgentConfigSanitizer.CollectObjectKeys(doc.RootElement);
            if (ids.Count > 0)
                facts.Add(new AgentConfigFact("已保存凭据 Provider", string.Join(", ", ids)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpencodeConfigSource auth.json: {ex.Message}");
            notes.Add(AgentConfigNotes.ParseFailed);
            state = AgentProviderConfigReport.Partial;
        }
    }

    private static string? DefaultConfigFile()
    {
        var overridden = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var dir = DefaultConfigDir();
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        var json = Path.Combine(dir, "opencode.json");
        if (File.Exists(json))
            return json;

        var jsonc = Path.Combine(dir, "opencode.jsonc");
        return File.Exists(jsonc) ? jsonc : json;
    }

    private static string? DefaultConfigDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;

        // XDG-style on Windows as used by OpenCode: ~/.config/opencode
        return Path.Combine(home, ".config", "opencode");
    }

    private static string? DefaultAuthRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;

        // Windows OpenCode auth store (observed): %USERPROFILE%\.local\share\opencode
        return Path.Combine(home, ".local", "share", "opencode");
    }
}
