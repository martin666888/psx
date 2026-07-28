namespace PSX.Services;

public interface IAgentRuntimeCoordinator
{
    Task PrepareForStartupAsync(CancellationToken cancellationToken = default);
    Task RefreshReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// User-requested update check for one runtime. Process-wide single-flight
    /// per runtime instance: concurrent requests attach to the in-flight
    /// operation instead of starting a second npm run.
    /// </summary>
    Task<AcpRuntimeOperationResult> RequestUpdateAsync(IAcpAgentRuntime runtime);
}

public sealed class AgentRuntimeCoordinator : IAgentRuntimeCoordinator, IDisposable
{
    private readonly IReadOnlyList<IAcpAgentRuntime> _runtimes;
    private readonly object _updateLock = new();
    private readonly Dictionary<IAcpAgentRuntime, Task<AcpRuntimeOperationResult>> _inFlightUpdates =
        new(ReferenceEqualityComparer.Instance);

    public AgentRuntimeCoordinator(IAgentProviderRegistry providers)
    {
        _runtimes = providers.Providers
            .Select(provider => provider.Runtime)
            .Distinct<IAcpAgentRuntime>(ReferenceEqualityComparer.Instance)
            .ToArray();
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _runtimes)
            await runtime.PrepareForStartupAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshReadyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _runtimes)
        {
            if (runtime.IsReady())
                await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<AcpRuntimeOperationResult> RequestUpdateAsync(IAcpAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_updateLock)
        {
            if (_inFlightUpdates.TryGetValue(runtime, out var inFlight))
                return inFlight;

            var update = RunUpdateAsync(runtime);
            // Only track operations that are still running; a synchronously
            // completed refresh must not be replayed to the next requester.
            if (!update.IsCompleted)
                _inFlightUpdates[runtime] = update;
            return update;
        }
    }

    private async Task<AcpRuntimeOperationResult> RunUpdateAsync(IAcpAgentRuntime runtime)
    {
        try
        {
            // No caller token: the shared operation must not die because one
            // of the attached requesters went away.
            return await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_updateLock)
            {
                _inFlightUpdates.Remove(runtime);
            }
        }
    }

    public void Dispose() { }
}
