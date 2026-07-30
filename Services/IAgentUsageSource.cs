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
    /// Collect exact usage for the given PSX session IDs. Returns parsed
    /// records plus a completeness status; implementations must not throw for
    /// missing directories or malformed data — they report it in the status.
    /// </summary>
    AgentUsageContribution Collect(IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken);
}
