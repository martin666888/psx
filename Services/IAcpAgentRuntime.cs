using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Version facts shared by every runtime for the toolbar Update surface:
/// what is live now, what is staged for the next launch, and whether a
/// staged update is waiting for a restart.
/// </summary>
public sealed record RuntimeVersionSnapshot(
    string? CurrentVersion,
    string? PendingVersion,
    bool HasPendingUpdate);

public interface IAcpAgentRuntime : IDisposable
{
    /// <summary>
    /// Reports runtime progress and readiness changes. Implementations must
    /// raise this after an installation makes <see cref="IsReady"/> become
    /// true so every workspace sharing this runtime can refresh and resume.
    /// Different provider dependency sets should use different runtime
    /// instances; providers intentionally sharing packages should share one.
    /// </summary>
    event Action<string>? StatusChanged;

    string LogPath { get; }

    /// <summary>
    /// True when this runtime can download updates itself (the two-directory
    /// npm staging model). False for runtimes that only ship with PSX
    /// releases; the toolbar renders those as "updates ship with PSX".
    /// </summary>
    bool SupportsSelfUpdate { get; }

    bool IsReady();
    string BuildStatusText(string? suffix = null);

    RuntimeVersionSnapshot GetVersionSnapshot();

    Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default);

    Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default);

    Task PrepareForStartupAsync(CancellationToken cancellationToken = default);

    AcpProcessSpec CreateProcessSpec(string workingDirectory);
}
