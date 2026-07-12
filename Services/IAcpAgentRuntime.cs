using PSX.Models;

namespace PSX.Services;

public interface IAcpAgentRuntime : IDisposable
{
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
