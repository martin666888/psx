using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Provider-declared exact-usage data source for the global Usage panel.
/// Implementations attribute precise token usage to a set of PSX session IDs
/// (PSX-only scope): they must never report usage for sessions PSX did not
/// create. This lives at the provider layer so shared aggregation code never
/// branches on provider identity.
/// </summary>
public interface IAgentUsageSource
{
    /// <summary>
    /// Selects the single persisted session identifier understood by this
    /// source. Keeping this choice in the Provider layer prevents shared
    /// aggregation from branching on Provider names or double-counting legacy
    /// and ACP identifiers for the same thread.
    /// </summary>
    string? ResolveSessionId(AgentUsageThreadSnapshot thread) =>
        thread.AcpSessionId ?? thread.ClaudeSessionId;

    /// <summary>
    /// Collect exact usage for the given PSX session IDs. Records are emitted
    /// immediately to keep scans bounded; implementations must not throw for
    /// missing directories or malformed data — they report it in the status.
    /// </summary>
    AgentUsageSourceStatus Collect(
        IReadOnlyCollection<string> sessionIds,
        IAgentUsageRecordSink sink,
        CancellationToken cancellationToken);
}

/// <summary>Streaming target owned by the provider-agnostic aggregator.</summary>
public interface IAgentUsageRecordSink
{
    void Add(AgentUsageRecord record);
}
