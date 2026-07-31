namespace PSX.Models;

// Data contracts for the global Usage panel. All records serialize to camelCase
// at the bridge boundary (see AgentWorkspaceCoordinator usage handler).

/// <summary>Lightweight, read-only projection of one stored thread for usage
/// aggregation. Never triggers an index rewrite.</summary>
public sealed record AgentUsageThreadSnapshot(
    string Provider,
    string? ClaudeSessionId,
    string? AcpSessionId);

/// <summary>Result of enumerating every thread file. <see cref="SkippedFiles"/>
/// counts unreadable/corrupt thread JSON so folded completeness can report a
/// gap instead of silently under-counting.</summary>
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

    /// <summary>Stable, user-safe reason keys. Paths, provider internals and
    /// parser diagnostics stay in <see cref="Detail"/> and never cross the
    /// bridge.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

public static class AgentUsageGapReason
{
    public const string UnsupportedSource = "unsupported_source";
    public const string UnsupportedFormat = "unsupported_format";
    public const string MissingSessionLogs = "missing_session_logs";
    public const string AmbiguousSessionLogs = "ambiguous_session_logs";
    public const string UnreadableLogs = "unreadable_logs";
    public const string UnmatchedSessions = "unmatched_sessions";
    public const string MissingSessionId = "missing_session_id";
    public const string DamagedThreadFiles = "damaged_thread_files";
    public const string UnregisteredProvider = "unregistered_provider";

    public static readonly IReadOnlyList<string> Ordered =
    [
        UnsupportedSource,
        UnsupportedFormat,
        MissingSessionLogs,
        AmbiguousSessionLogs,
        UnreadableLogs,
        UnmatchedSessions,
        MissingSessionId,
        DamagedThreadFiles,
        UnregisteredProvider
    ];
}

/// <summary>One independently computed report window. Token totals include
/// input, output, cache-read and cache-creation tokens from exact sources.</summary>
public sealed record AgentUsageWindow(long TotalTokens);

public sealed record AgentUsageReport(
    DateOnly HeatmapStartDate,
    IReadOnlyList<long> DailyTokens,
    AgentUsageWindow Today,
    AgentUsageWindow Last7Days,
    AgentUsageWindow Last30Days,
    IReadOnlyList<AgentProviderUsageReport> Providers);

public sealed record AgentProviderUsageReport(
    string ProviderKey,
    string DisplayName,
    string IconKey,
    IReadOnlyList<long> DailyTokens,
    AgentUsageWindow Today,
    AgentUsageWindow Last7Days,
    AgentUsageWindow Last30Days,
    AgentUsageCompleteness Completeness);

/// <summary>User-facing completeness folded across local thread enumeration
/// and applicable exact-usage sources. Only stable reason keys cross the
/// bridge; parser details and paths remain internal.</summary>
public sealed record AgentUsageCompleteness(
    string Status,
    IReadOnlyList<string> Reasons,
    int? ExpectedSessions,
    int? MatchedSessions,
    int SkippedFiles,
    int BadLines,
    int UntrackedThreads)
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

/// <summary>Top-level payload for an agent_usage_report event body.</summary>
public sealed record AgentUsageResult(
    DateTimeOffset GeneratedAt,
    string Timezone,
    AgentUsageReport Report,
    AgentUsageCompleteness Completeness);
