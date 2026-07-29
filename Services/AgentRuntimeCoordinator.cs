namespace PSX.Services;

/// <summary>
/// Broadcast payload for user-requested runtime updates. Raised for every
/// phase (checking, progress, outcome) so every workspace sharing the same
/// runtime renders the same Update button state.
/// </summary>
public sealed class RuntimeUpdateStatusChangedEventArgs : EventArgs
{
    public required IAcpAgentRuntime Runtime { get; init; }
    public required string State { get; init; }
    public string Message { get; init; } = "";
}

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

    /// <summary>
    /// Update lifecycle broadcast: checking on start, checking with a progress
    /// message while npm runs, then up_to_date / staged_restart_required /
    /// failed. Sessions forward the states matching their own runtime.
    /// </summary>
    event EventHandler<RuntimeUpdateStatusChangedEventArgs>? UpdateStatusChanged;

    /// <summary>True while a user-requested update runs for this runtime.</summary>
    bool IsUpdateInFlight(IAcpAgentRuntime runtime);
}

public sealed class AgentRuntimeCoordinator : IAgentRuntimeCoordinator, IDisposable
{
    private readonly IReadOnlyList<IAcpAgentRuntime> _runtimes;
    private readonly List<(IAcpAgentRuntime Runtime, Action<string> Handler)> _statusSubscriptions = new();
    private readonly object _updateLock = new();
    private readonly Dictionary<IAcpAgentRuntime, Task<AcpRuntimeOperationResult>> _inFlightUpdates =
        new(ReferenceEqualityComparer.Instance);

    public event EventHandler<RuntimeUpdateStatusChangedEventArgs>? UpdateStatusChanged;

    public AgentRuntimeCoordinator(IAgentProviderRegistry providers)
    {
        _runtimes = providers.Providers
            .Select(provider => provider.Runtime)
            .Distinct<IAcpAgentRuntime>(ReferenceEqualityComparer.Instance)
            .ToArray();

        // npm progress arrives through the runtime's own StatusChanged; while
        // an update is in flight it is re-broadcast as "checking" progress.
        foreach (var runtime in _runtimes)
        {
            Action<string> handler = message =>
            {
                if (IsUpdateInFlight(runtime))
                    RaiseUpdateStatus(runtime, "checking", message);
            };
            runtime.StatusChanged += handler;
            _statusSubscriptions.Add((runtime, handler));
        }
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

    public bool IsUpdateInFlight(IAcpAgentRuntime runtime)
    {
        lock (_updateLock)
        {
            return _inFlightUpdates.ContainsKey(runtime);
        }
    }

    private async Task<AcpRuntimeOperationResult> RunUpdateAsync(IAcpAgentRuntime runtime)
    {
        RaiseUpdateStatus(runtime, "checking", "");
        try
        {
            // No caller token: the shared operation must not die because one
            // of the attached requesters went away.
            var result = await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            var state = result.Kind is AcpRuntimeOperationKind.Success or AcpRuntimeOperationKind.AlreadyReady
                ? (runtime.GetVersionSnapshot().HasPendingUpdate ? "staged_restart_required" : "up_to_date")
                : "failed";
            RaiseUpdateStatus(runtime, state, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            var result = new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, ex.Message);
            RaiseUpdateStatus(runtime, "failed", ex.Message);
            return result;
        }
        finally
        {
            lock (_updateLock)
            {
                _inFlightUpdates.Remove(runtime);
            }
        }
    }

    private void RaiseUpdateStatus(IAcpAgentRuntime runtime, string state, string message)
    {
        UpdateStatusChanged?.Invoke(this, new RuntimeUpdateStatusChangedEventArgs
        {
            Runtime = runtime,
            State = state,
            Message = message
        });
    }

    public void Dispose()
    {
        foreach (var (runtime, handler) in _statusSubscriptions)
            runtime.StatusChanged -= handler;
        _statusSubscriptions.Clear();
    }
}
