using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Descriptor of a local-machine usage contributor. Unlike
/// <see cref="IAgentUsageSource"/> (PSX-session scope), a local contributor
/// counts every matching session found on this machine, including sessions
/// started outside PSX. <see cref="TrackedProviderKey"/> optionally names the
/// ACP provider whose ThreadStore threads this contributor claims, so those
/// threads count as tracked without being attributed a second time.
/// </summary>
public sealed record AgentLocalUsageDescriptor(
    string Key,
    string DisplayName,
    string IconKey,
    string? TrackedProviderKey);

/// <summary>
/// Exact-usage contribution source with <see cref="AgentUsageScope.LocalAll"/>
/// scope. Implementations scan local session logs independently of the
/// ThreadStore, stream records into the sink (bounded, never retaining full
/// history), and report gaps through fixed reason keys only. A contributor
/// always appears in the report: a missing or empty session directory is
/// "available with zero data", never an error.
/// </summary>
public interface IAgentLocalUsageContributor
{
    AgentLocalUsageDescriptor Descriptor { get; }

    AgentUsageSourceStatus Collect(
        IAgentUsageRecordSink sink,
        CancellationToken cancellationToken);
}
