namespace PSX.Models;

// Data contracts for the global Usage panel. All records serialize to camelCase
// at the bridge boundary (see AgentWorkspaceCoordinator usage handler).

/// <summary>Lightweight, read-only projection of one stored thread for usage
/// aggregation. Never triggers an index rewrite.</summary>
public sealed record AgentUsageThreadSnapshot(
    string ThreadId,
    string Title,
    string Provider,
    string? ClaudeSessionId,
    string? AcpSessionId,
    long? ContextUsedTokens,
    long? ContextWindowTokens,
    IReadOnlyList<AgentUsageMessageStamp> Messages);

/// <summary>One message reduced to the fields usage aggregation needs.</summary>
public sealed record AgentUsageMessageStamp(string Role, DateTimeOffset CreatedAt);

/// <summary>Result of enumerating every thread file. <see cref="SkippedFiles"/>
/// counts unreadable/corrupt thread JSON so the psx-threads source can report
/// partial completeness instead of silently under-counting.</summary>
public sealed record AgentThreadUsageSnapshot(
    IReadOnlyList<AgentUsageThreadSnapshot> Threads,
    int ScannedFiles,
    int SkippedFiles);

/// <summary>One exact-usage datapoint parsed from a provider session log.</summary>
public sealed record AgentUsageRecord(
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens);

/// <summary>Completeness status for one usage data source. Distinguishes
/// "available with no data" (all-zero) from "unavailable" (error/missing).</summary>
public sealed record AgentUsageSourceStatus(
    string Key,
    string Status,
    int ScannedFiles,
    int SkippedFiles,
    int BadLines,
    int? ExpectedSessions,
    int? MatchedSessions,
    string ParserVersion,
    DateTimeOffset LastScanAt,
    string? Detail)
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

/// <summary>Everything one <see cref="PSX.Services.IAgentUsageSource"/> returns:
/// the parsed records plus its own completeness status.</summary>
public sealed record AgentUsageContribution(
    IReadOnlyList<AgentUsageRecord> Records,
    AgentUsageSourceStatus Status);

public sealed record AgentUsageTokens(long Input, long Output, long CacheRead, long CacheCreation)
{
    public static readonly AgentUsageTokens Zero = new(0, 0, 0, 0);
}

public sealed record AgentUsageModelRow(
    string Model, long Input, long Output, long CacheRead, long CacheCreation);

public sealed record AgentUsageExactUsage(IReadOnlyList<AgentUsageModelRow> ModelRows);

public sealed record AgentUsageContextSnapshot(string ThreadTitle, long? UsedTokens, long? WindowTokens);

/// <summary>One provider's slice of a window. Rendering is purely field-driven:
/// <see cref="ExactUsage"/> present -> model table; <see cref="ContextSnapshots"/>
/// present -> snapshot list. No consumer inspects <see cref="ProviderKey"/>.</summary>
public sealed record AgentUsageProviderSection(
    string ProviderKey,
    string IconKey,
    AgentUsageExactUsage? ExactUsage,
    IReadOnlyList<AgentUsageContextSnapshot>? ContextSnapshots,
    string SourceKey);

/// <summary>Aggregates for one time window. <see cref="Tokens"/> and
/// <see cref="CacheHitRate"/> sum exact usage only; context snapshots never
/// contribute to totals.</summary>
public sealed record AgentUsageWindow(
    int ActiveThreads,
    int Turns,
    AgentUsageTokens Tokens,
    double? CacheHitRate,
    IReadOnlyList<AgentUsageProviderSection> ProviderSections);

public sealed record AgentUsageReport(
    IReadOnlyList<int> Heatmap,
    DateOnly HeatmapStartDate,
    AgentUsageWindow Today,
    AgentUsageWindow Last7Days,
    AgentUsageWindow Last30Days);

/// <summary>Top-level payload for an agent_usage_report event body.</summary>
public sealed record AgentUsageResult(
    DateTimeOffset GeneratedAt,
    string Timezone,
    AgentUsageReport Report,
    IReadOnlyList<AgentUsageSourceStatus> Sources);
