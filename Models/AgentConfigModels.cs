namespace PSX.Models;

// Data contracts for the global Usage-panel「配置」tab. All records serialize
// to camelCase at the bridge boundary (see AgentWorkspaceCoordinator config
// handler). Secrets never appear in these DTOs — only masked key names and
// sanitized targets.

/// <summary>Top-level payload for an agent_config_report event body.</summary>
public sealed record AgentConfigResult(
    DateTimeOffset GeneratedAt,
    AgentConfigReport Report);

public sealed record AgentConfigReport(
    IReadOnlyList<AgentProviderConfigReport> Providers);

public sealed record AgentProviderConfigReport(
    string ProviderKey,
    string DisplayName,
    string IconKey,
    string State,
    IReadOnlyList<AgentConfigFact> Facts,
    IReadOnlyList<AgentConfigModelEntry> Models,
    IReadOnlyList<AgentConfigMcpServer> McpServers,
    IReadOnlyList<AgentConfigSkill> Skills,
    IReadOnlyList<string> Notes)
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

/// <summary>Open key/value fact rendered as plain text in the Config panel.
/// <see cref="LabelKey"/> is one of the fixed <see cref="AgentConfigFactLabels"/>
/// keys; values stay raw data (model names, key names, counts).</summary>
public sealed record AgentConfigFact(string LabelKey, string Value);

/// <summary>Fixed wire keys for <see cref="AgentConfigFact.LabelKey"/>. The
/// frontend maps each to display copy; label sentences never cross the bridge.</summary>
public static class AgentConfigFactLabels
{
    public const string EnvVarKeys = "config.fact.env_var_keys";
    public const string DefaultModel = "config.fact.default_model";
    public const string SmallModel = "config.fact.small_model";
    public const string SubagentModel = "config.fact.subagent_model";
    public const string SubagentModelPool = "config.fact.subagent_model_pool";
    public const string DefaultPlanMode = "config.fact.default_plan_mode";
    public const string AuthType = "config.fact.auth_type";
    public const string EnabledPlugins = "config.fact.enabled_plugins";
    public const string PluginCount = "config.fact.plugin_count";
    public const string CustomProviders = "config.fact.custom_providers";
    public const string DisabledProviders = "config.fact.disabled_providers";
    public const string SavedCredentialProviders = "config.fact.saved_credential_providers";
    public const string LocalPermissionsAllow = "config.fact.local_permissions_allow";
    public const string InstructionFileCount = "config.fact.instruction_file_count";
    public const string Instructions = "config.fact.instructions";
}

/// <summary>One configured model. <see cref="BaseUrl"/> is URL-sanitized.</summary>
public sealed record AgentConfigModelEntry(
    string Id,
    string? Name,
    string? BaseUrl);

/// <summary>One MCP server entry after secret stripping.</summary>
public sealed record AgentConfigMcpServer(
    string Name,
    string Transport,
    string Target,
    bool Enabled,
    IReadOnlyList<string> EnvKeys,
    IReadOnlyList<string> HeaderKeys)
{
    public const string TransportStdio = "stdio";
    public const string TransportHttp = "http";
    public const string TransportSse = "sse";
}

public sealed record AgentConfigSkill(string Name);

/// <summary>Fixed, user-safe note codes that may cross the bridge; the
/// frontend maps each to display copy.</summary>
public static class AgentConfigNotes
{
    public const string FileTooLarge = "config.note.file_too_large";
    public const string ParseFailed = "config.note.parse_failed";
    public const string NoEditableConfig = "config.note.no_editable_config";
    public const string HomeMissing = "config.note.home_missing";
    public const string ScanFailed = "config.note.scan_failed";
}
