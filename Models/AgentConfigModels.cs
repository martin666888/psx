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

/// <summary>Open key/value fact rendered as plain text in the Config panel.</summary>
public sealed record AgentConfigFact(string Label, string Value);

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

/// <summary>Fixed, user-safe notes that may cross the bridge.</summary>
public static class AgentConfigNotes
{
    public const string FileTooLarge = "配置文件过大，已跳过";
    public const string ParseFailed = "部分配置无法解析";
    public const string NoEditableConfig = "该 Agent 无用户可编辑配置文件";
    public const string HomeMissing = "未找到用户配置目录";
    public const string ScanFailed = "无法读取配置信息，请重试。";
}
