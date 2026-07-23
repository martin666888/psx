using PSX.Models;

namespace PSX.Services;

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
    bool IsReady();
    string BuildStatusText(string? suffix = null);

    Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default);

    Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default);

    Task PrepareForStartupAsync(CancellationToken cancellationToken = default);

    AcpProcessSpec CreateProcessSpec(string workingDirectory);
}
