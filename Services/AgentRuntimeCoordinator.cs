using PSX.Models;

namespace PSX.Services;

public sealed class RuntimeInstallStatusChangedEventArgs : EventArgs
{
    public required IAcpAgentRuntime Runtime { get; init; }
    public required string State { get; init; }
    public string MessageCode { get; init; } = "";
}

public sealed record RuntimeInstallSnapshot(string State, string MessageCode);

public sealed class RuntimeUpdateStatusChangedEventArgs : EventArgs
{
    public required IAcpAgentRuntime Runtime { get; init; }
    public required string State { get; init; }
    public string MessageCode { get; init; } = "";
}

public sealed record RuntimeUpdateSnapshot(string State, string MessageCode);

public interface IAgentRuntimeCoordinator
{
    Task PrepareForStartupAsync(CancellationToken cancellationToken = default);

    Task<AcpRuntimeOperationResult> RequestInstallAsync(IAcpAgentRuntime runtime);
    void CancelInstall(IAcpAgentRuntime runtime);
    bool IsInstallInFlight(IAcpAgentRuntime runtime);
    RuntimeInstallSnapshot? GetInstallSnapshot(IAcpAgentRuntime runtime);
    event EventHandler<RuntimeInstallStatusChangedEventArgs>? InstallStatusChanged;

    Task<AcpRuntimeOperationResult> RequestUpdateAsync(IAcpAgentRuntime runtime);
    event EventHandler<RuntimeUpdateStatusChangedEventArgs>? UpdateStatusChanged;
    bool IsUpdateInFlight(IAcpAgentRuntime runtime);
    RuntimeUpdateSnapshot? GetUpdateSnapshot(IAcpAgentRuntime runtime);

    bool HasOperationsInFlight { get; }
    Task CancelAllOperationsAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentRuntimeCoordinator : IAgentRuntimeCoordinator, IDisposable
{
    private readonly IReadOnlyList<IAcpAgentRuntime> _runtimes;
    private readonly List<(IAcpAgentRuntime Runtime, Action<string> Handler)> _statusSubscriptions = new();
    private readonly object _operationLock = new();
    private readonly object _statusPublishLock = new();
    private readonly Dictionary<IAcpAgentRuntime, Task<AcpRuntimeOperationResult>> _inFlightInstalls =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAcpAgentRuntime, CancellationTokenSource> _installCancellations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAcpAgentRuntime, RuntimeInstallSnapshot> _installSnapshots =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAcpAgentRuntime, Task<AcpRuntimeOperationResult>> _inFlightUpdates =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAcpAgentRuntime, CancellationTokenSource> _updateCancellations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IAcpAgentRuntime, RuntimeUpdateSnapshot> _updateSnapshots =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IAcpAgentRuntime> _suppressUpdateCompletion =
        new(ReferenceEqualityComparer.Instance);

    public event EventHandler<RuntimeInstallStatusChangedEventArgs>? InstallStatusChanged;
    public event EventHandler<RuntimeUpdateStatusChangedEventArgs>? UpdateStatusChanged;

    public AgentRuntimeCoordinator(IAgentProviderRegistry providers)
    {
        _runtimes = providers.Providers
            .Select(provider => provider.Runtime)
            .Distinct<IAcpAgentRuntime>(ReferenceEqualityComparer.Instance)
            .ToArray();

        foreach (var runtime in _runtimes)
        {
            Action<string> handler = _ => PublishRuntimeProgress(runtime);
            runtime.StatusChanged += handler;
            _statusSubscriptions.Add((runtime, handler));
        }
    }

    public bool HasOperationsInFlight
    {
        get
        {
            lock (_operationLock)
                return _inFlightInstalls.Count > 0 || _inFlightUpdates.Count > 0;
        }
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _runtimes)
            await runtime.PrepareForStartupAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<AcpRuntimeOperationResult> RequestInstallAsync(IAcpAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        AcpRuntimeOperationResult? alreadyReady = null;
        TaskCompletionSource<AcpRuntimeOperationResult>? completion = null;
        CancellationTokenSource? cancellation = null;
        lock (_statusPublishLock)
        {
            lock (_operationLock)
            {
                if (_inFlightInstalls.TryGetValue(runtime, out var inFlight))
                    return inFlight;
                if (runtime.IsReady())
                {
                    alreadyReady = new AcpRuntimeOperationResult(
                        AcpRuntimeOperationKind.AlreadyReady,
                        "Agent runtime is already installed.");
                    _installSnapshots[runtime] = new RuntimeInstallSnapshot(
                        "ready",
                        RuntimeStatusCode.Ready);
                }
                else
                {
                    completion = new TaskCompletionSource<AcpRuntimeOperationResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    cancellation = new CancellationTokenSource();
                    _inFlightInstalls[runtime] = completion.Task;
                    _installCancellations[runtime] = cancellation;
                    _installSnapshots[runtime] = new RuntimeInstallSnapshot(
                        "installing",
                        RuntimeStatusCode.PreparingInstall);
                }
            }

            PublishInstallStatus(
                runtime,
                alreadyReady is null ? "installing" : "ready",
                alreadyReady is null ? RuntimeStatusCode.PreparingInstall : RuntimeStatusCode.Ready);
        }

        if (alreadyReady is not null)
            return Task.FromResult(alreadyReady);

        _ = RunInstallAsync(runtime, cancellation!, completion!);
        return completion!.Task;
    }

    public void CancelInstall(IAcpAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        CancellationTokenSource? cancellation;
        lock (_statusPublishLock)
        {
            lock (_operationLock)
            {
                if (!_installCancellations.TryGetValue(runtime, out cancellation))
                    return;
                _installSnapshots[runtime] = new RuntimeInstallSnapshot(
                    "installing",
                    RuntimeStatusCode.CancellingInstall);
            }
            PublishInstallStatus(runtime, "installing", RuntimeStatusCode.CancellingInstall);
        }

        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public bool IsInstallInFlight(IAcpAgentRuntime runtime)
    {
        lock (_operationLock)
            return _inFlightInstalls.ContainsKey(runtime);
    }

    public RuntimeInstallSnapshot? GetInstallSnapshot(IAcpAgentRuntime runtime)
    {
        lock (_operationLock)
            return _installSnapshots.TryGetValue(runtime, out var snapshot) ? snapshot : null;
    }

    private async Task RunInstallAsync(
        IAcpAgentRuntime runtime,
        CancellationTokenSource cancellation,
        TaskCompletionSource<AcpRuntimeOperationResult> completion)
    {
        var result = new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed,
            "Runtime installation ended unexpectedly.");
        try
        {
            try
            {
                result = await runtime.EnsureInstalledAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Cancelled,
                    "Runtime installation was cancelled.");
            }
            catch (Exception ex)
            {
                result = new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, ex.Message);
            }
        }
        finally
        {
            var state = result.Kind is AcpRuntimeOperationKind.Success or AcpRuntimeOperationKind.AlreadyReady
                ? "ready"
                : result.Kind == AcpRuntimeOperationKind.Cancelled ? "cancelled" : "failed";
            var messageCode = result.Kind switch
            {
                AcpRuntimeOperationKind.Success or AcpRuntimeOperationKind.AlreadyReady => RuntimeStatusCode.InstallSucceeded,
                AcpRuntimeOperationKind.Cancelled => RuntimeStatusCode.InstallCancelled,
                AcpRuntimeOperationKind.NetworkUnavailable => RuntimeStatusCode.NetworkUnavailable,
                _ => RuntimeStatusCode.InstallFailed
            };

            try
            {
                CompleteInstallStatus(runtime, cancellation, state, messageCode);
            }
            finally
            {
                completion.TrySetResult(result);
            }
        }
    }

    public Task<AcpRuntimeOperationResult> RequestUpdateAsync(IAcpAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        AcpRuntimeOperationResult? alreadyStaged = null;
        TaskCompletionSource<AcpRuntimeOperationResult>? completion = null;
        CancellationTokenSource? cancellation = null;
        lock (_statusPublishLock)
        {
            lock (_operationLock)
            {
                if (_inFlightUpdates.TryGetValue(runtime, out var inFlight))
                    return inFlight;
                if (_inFlightInstalls.ContainsKey(runtime))
                {
                    return Task.FromResult(new AcpRuntimeOperationResult(
                        AcpRuntimeOperationKind.Failed,
                        "Install the Agent runtime before checking for updates."));
                }
                if (runtime.GetVersionSnapshot().HasPendingUpdate)
                {
                    alreadyStaged = new AcpRuntimeOperationResult(
                        AcpRuntimeOperationKind.AlreadyReady,
                        "Update already staged. Restart PSX to apply it.");
                    _updateSnapshots[runtime] = new RuntimeUpdateSnapshot(
                        "staged_restart_required",
                        "");
                }
                else
                {
                    completion = new TaskCompletionSource<AcpRuntimeOperationResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    cancellation = new CancellationTokenSource();
                    _inFlightUpdates[runtime] = completion.Task;
                    _updateCancellations[runtime] = cancellation;
                }
            }

            if (alreadyStaged is not null)
                PublishUpdateStatus(runtime, "staged_restart_required", "");
        }

        if (alreadyStaged is not null)
            return Task.FromResult(alreadyStaged);

        _ = RunUpdateAsync(runtime, cancellation!, completion!);
        return completion!.Task;
    }

    public bool IsUpdateInFlight(IAcpAgentRuntime runtime)
    {
        lock (_operationLock)
            return _inFlightUpdates.ContainsKey(runtime);
    }

    public RuntimeUpdateSnapshot? GetUpdateSnapshot(IAcpAgentRuntime runtime)
    {
        lock (_operationLock)
            return _updateSnapshots.TryGetValue(runtime, out var snapshot) ? snapshot : null;
    }

    private async Task RunUpdateAsync(
        IAcpAgentRuntime runtime,
        CancellationTokenSource cancellation,
        TaskCompletionSource<AcpRuntimeOperationResult> completion)
    {
        var result = new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed,
            "Runtime update ended unexpectedly.");
        var finalState = "failed";
        var finalMessageCode = RuntimeStatusCode.UpdateFailed;
        try
        {
            RaiseUpdateStatus(runtime, "checking", "");
            try
            {
                result = await runtime.RefreshAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Cancelled,
                    "Runtime update was cancelled during shutdown.");
            }
            catch (Exception ex)
            {
                result = new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, ex.Message);
            }

            finalState = result.Kind is AcpRuntimeOperationKind.Success or AcpRuntimeOperationKind.AlreadyReady
                ? (runtime.GetVersionSnapshot().HasPendingUpdate ? "staged_restart_required" : "up_to_date")
                : "failed";
            finalMessageCode = finalState == "failed"
                ? (result.Kind == AcpRuntimeOperationKind.NetworkUnavailable
                    ? RuntimeStatusCode.UpdateNetworkUnavailable
                    : RuntimeStatusCode.UpdateFailed)
                : "";
        }
        finally
        {
            try
            {
                CompleteUpdateStatus(runtime, cancellation, finalState, finalMessageCode);
            }
            finally
            {
                completion.TrySetResult(result);
            }
        }
    }

    public async Task CancelAllOperationsAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource[] cancellations;
        Task[] operations;
        lock (_operationLock)
        {
            foreach (var runtime in _inFlightUpdates.Keys)
                _suppressUpdateCompletion.Add(runtime);
            cancellations = _installCancellations.Values
                .Concat(_updateCancellations.Values)
                .ToArray();
            operations = _inFlightInstalls.Values
                .Concat(_inFlightUpdates.Values)
                .Cast<Task>()
                .ToArray();
        }

        foreach (var source in cancellations)
        {
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (operations.Length > 0)
            await Task.WhenAll(operations).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishRuntimeProgress(IAcpAgentRuntime runtime)
    {
        lock (_statusPublishLock)
        {
            RuntimeInstallSnapshot? installSnapshot = null;
            var publishUpdate = false;
            lock (_operationLock)
            {
                if (_inFlightInstalls.ContainsKey(runtime))
                {
                    installSnapshot = _installSnapshots.TryGetValue(runtime, out var snapshot)
                        ? snapshot
                        : new RuntimeInstallSnapshot("installing", RuntimeStatusCode.PreparingInstall);
                }
                else if (_inFlightUpdates.ContainsKey(runtime))
                {
                    _updateSnapshots[runtime] = new RuntimeUpdateSnapshot("checking", "");
                    publishUpdate = true;
                }
            }

            if (installSnapshot is not null)
                PublishInstallStatus(runtime, installSnapshot.State, installSnapshot.MessageCode);
            else if (publishUpdate)
                PublishUpdateStatus(runtime, "checking", "");
        }
    }

    private void CompleteInstallStatus(
        IAcpAgentRuntime runtime,
        CancellationTokenSource cancellation,
        string state,
        string messageCode)
    {
        lock (_statusPublishLock)
        {
            lock (_operationLock)
            {
                _inFlightInstalls.Remove(runtime);
                _installCancellations.Remove(runtime);
                _installSnapshots[runtime] = new RuntimeInstallSnapshot(state, messageCode);
            }
            cancellation.Dispose();
            PublishInstallStatus(runtime, state, messageCode);
        }
    }

    private void PublishInstallStatus(IAcpAgentRuntime runtime, string state, string messageCode) =>
        InstallStatusChanged?.Invoke(this, new RuntimeInstallStatusChangedEventArgs
        {
            Runtime = runtime,
            State = state,
            MessageCode = messageCode
        });

    private void CompleteUpdateStatus(
        IAcpAgentRuntime runtime,
        CancellationTokenSource cancellation,
        string state,
        string messageCode)
    {
        lock (_statusPublishLock)
        {
            bool publish;
            lock (_operationLock)
            {
                _inFlightUpdates.Remove(runtime);
                _updateCancellations.Remove(runtime);
                publish = !_suppressUpdateCompletion.Remove(runtime);
                if (publish)
                    _updateSnapshots[runtime] = new RuntimeUpdateSnapshot(state, messageCode);
                else
                    _updateSnapshots.Remove(runtime);
            }
            cancellation.Dispose();
            if (publish)
                PublishUpdateStatus(runtime, state, messageCode);
        }
    }

    private void RaiseUpdateStatus(IAcpAgentRuntime runtime, string state, string messageCode)
    {
        lock (_statusPublishLock)
        {
            lock (_operationLock)
                _updateSnapshots[runtime] = new RuntimeUpdateSnapshot(state, messageCode);
            PublishUpdateStatus(runtime, state, messageCode);
        }
    }

    private void PublishUpdateStatus(IAcpAgentRuntime runtime, string state, string messageCode) =>
        UpdateStatusChanged?.Invoke(this, new RuntimeUpdateStatusChangedEventArgs
        {
            Runtime = runtime,
            State = state,
            MessageCode = messageCode
        });

    public void Dispose()
    {
        foreach (var (runtime, handler) in _statusSubscriptions)
            runtime.StatusChanged -= handler;
        _statusSubscriptions.Clear();

        CancellationTokenSource[] cancellations;
        lock (_operationLock)
            cancellations = _installCancellations.Values.Concat(_updateCancellations.Values).ToArray();
        foreach (var source in cancellations)
        {
            try { source.Cancel(); } catch { }
        }
    }
}
