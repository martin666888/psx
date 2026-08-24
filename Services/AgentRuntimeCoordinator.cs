using PSX.Models;

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
    /// <summary>Fixed <see cref="RuntimeStatusCode"/> key or "" — never a
    /// composed sentence; display copy lives in the frontend.</summary>
    public string MessageCode { get; init; } = "";
}

/// <summary>
/// Process-wide snapshot of the last update lifecycle state for one runtime.
/// Lets workspaces created or re-activated after an update finished show the
/// real outcome (failed / up_to_date) instead of defaulting back to idle.
/// </summary>
public sealed record RuntimeUpdateSnapshot(string State, string MessageCode);

public interface IAgentRuntimeCoordinator
{
    /// <summary>
    /// Startup-only local promotion of already-staged updates. Never touches
    /// the network: runtime updates are strictly user-triggered.
    /// </summary>
    Task PrepareForStartupAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Last broadcast lifecycle state for this runtime in this process, or
    /// null when no update has been requested yet.
    /// </summary>
    RuntimeUpdateSnapshot? GetUpdateSnapshot(IAcpAgentRuntime runtime);
}

public sealed class AgentRuntimeCoordinator : IAgentRuntimeCoordinator, IDisposable
{
    private readonly IReadOnlyList<IAcpAgentRuntime> _runtimes;
    private readonly List<(IAcpAgentRuntime Runtime, Action<string> Handler)> _statusSubscriptions = new();
    private readonly object _updateLock = new();
    private readonly Dictionary<IAcpAgentRuntime, Task<AcpRuntimeOperationResult>> _inFlightUpdates =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAcpAgentRuntime, RuntimeUpdateSnapshot> _updateSnapshots =
        new(ReferenceEqualityComparer.Instance);

    public event EventHandler<RuntimeUpdateStatusChangedEventArgs>? UpdateStatusChanged;

    public AgentRuntimeCoordinator(IAgentProviderRegistry providers)
    {
        _runtimes = providers.Providers
            .Select(provider => provider.Runtime)
            .Distinct<IAcpAgentRuntime>(ReferenceEqualityComparer.Instance)
            .ToArray();

        // npm progress arrives through the runtime's own StatusChanged; while
        // an update is in flight it is re-broadcast as a "checking" pulse so
        // late subscribers see activity. Progress detail stays internal —
        // the wire carries states plus fixed outcome codes only.
        foreach (var runtime in _runtimes)
        {
            Action<string> handler = _ =>
            {
                if (IsUpdateInFlight(runtime))
                    RaiseUpdateStatus(runtime, "checking", "");
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

    public Task<AcpRuntimeOperationResult> RequestUpdateAsync(IAcpAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // An already-staged update only needs a restart; never touch npm
        // again for it.
        if (runtime.GetVersionSnapshot().HasPendingUpdate)
        {
            var staged = new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Update already staged. Restart PSX to apply it.");
            RaiseUpdateStatus(runtime, "staged_restart_required", "");
            return Task.FromResult(staged);
        }

        TaskCompletionSource<AcpRuntimeOperationResult> completion;
        lock (_updateLock)
        {
            if (_inFlightUpdates.TryGetValue(runtime, out var inFlight))
                return inFlight;

            // The placeholder is registered before the runtime operation
            // starts so early StatusChanged progress is never dropped for
            // "not in flight yet". RunContinuationsAsynchronously keeps
            // workspace continuations out of the update lock and the
            // runtime's event call stack.
            completion = new TaskCompletionSource<AcpRuntimeOperationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightUpdates[runtime] = completion.Task;
        }

        _ = RunUpdateAsync(runtime, completion);
        return completion.Task;
    }

    public bool IsUpdateInFlight(IAcpAgentRuntime runtime)
    {
        lock (_updateLock)
        {
            return _inFlightUpdates.ContainsKey(runtime);
        }
    }

    public RuntimeUpdateSnapshot? GetUpdateSnapshot(IAcpAgentRuntime runtime)
    {
        lock (_updateLock)
        {
            return _updateSnapshots.TryGetValue(runtime, out var snapshot) ? snapshot : null;
        }
    }

    private async Task RunUpdateAsync(
        IAcpAgentRuntime runtime,
        TaskCompletionSource<AcpRuntimeOperationResult> completion)
    {
        var result = new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed,
            "Runtime update ended unexpectedly.");
        try
        {
            RaiseUpdateStatus(runtime, "checking", "");
            try
            {
                // No caller token: the shared operation must not die because
                // one of the attached requesters went away.
                result = await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, ex.Message);
            }

            var state = result.Kind is AcpRuntimeOperationKind.Success or AcpRuntimeOperationKind.AlreadyReady
                ? (runtime.GetVersionSnapshot().HasPendingUpdate ? "staged_restart_required" : "up_to_date")
                : "failed";
            // The wire carries a fixed outcome code derived from the result
            // kind; the raw result message stays internal (npm details,
            // exception text).
            var messageCode = state == "failed"
                ? (result.Kind == AcpRuntimeOperationKind.NetworkUnavailable
                    ? RuntimeStatusCode.UpdateNetworkUnavailable
                    : RuntimeStatusCode.UpdateFailed)
                : "";
            RaiseUpdateStatus(runtime, state, messageCode);
        }
        finally
        {
            // Drop the in-flight entry first, then complete on every path
            // (success, failure, broadcast exception) so no runtime is ever
            // stuck in a permanent "checking" state.
            lock (_updateLock)
            {
                _inFlightUpdates.Remove(runtime);
            }
            completion.TrySetResult(result);
        }
    }

    private void RaiseUpdateStatus(IAcpAgentRuntime runtime, string state, string messageCode)
    {
        lock (_updateLock)
        {
            _updateSnapshots[runtime] = new RuntimeUpdateSnapshot(state, messageCode);
        }
        UpdateStatusChanged?.Invoke(this, new RuntimeUpdateStatusChangedEventArgs
        {
            Runtime = runtime,
            State = state,
            MessageCode = messageCode
        });
    }

    public void Dispose()
    {
        foreach (var (runtime, handler) in _statusSubscriptions)
            runtime.StatusChanged -= handler;
        _statusSubscriptions.Clear();
    }
}
