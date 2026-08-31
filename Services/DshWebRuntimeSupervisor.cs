using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

public enum DshRuntimeState { NotInstalled, Installing, Starting, Ready, Exited, Failed }
public enum DshUpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Updating,
    Failed,
    /// <summary>Nothing newer is installable in this PSX build, but newer
    /// versions exist (deferred or blocked) — reporting up_to_date would be
    /// a lie and update commands must be refused without leaving this state.</summary>
    RequiresPsxUpdate
}
public enum DshUpdatePhase { Downloading, Validating, Restarting }

internal enum DshOperationKind { Install, Retry, CheckUpdate, Update, Recheck, RetryInstall }

/// <summary>
/// Process-level singleton supervising the `dsh web` server: install, launch
/// with a Job Object (KILL_ON_JOB_CLOSE), parse the ready signal, health-check,
/// and broadcast dsh_runtime_status. Workspace ≠ process.
/// </summary>
public sealed class DshWebRuntimeSupervisor : IDisposable
{
    private static readonly Regex ReadyPattern =
        new(@"dsh\s+web:\s+(https?://[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReadyTokenPattern = new(
        @"^[A-Za-z0-9._~-]{8,1024}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int MaximumReadyUrlLength = 2048;

    /// <summary>Bounded wait for the lifecycle lock during application
    /// shutdown; after it elapses the teardown runs without the lock instead
    /// of blocking the window close.</summary>
    private static readonly TimeSpan ShutdownLockTimeout = TimeSpan.FromSeconds(10);

    private readonly DshWebRuntime _runtime;
    private readonly IAgentBridgeService _bridge;
    private readonly PsxEnvironmentSettingsCoordinator _settings;
    private readonly string _logDirectory;
    private readonly string _workspaceDirectory;
    private readonly object _sync = new();
    private readonly object _publishLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly DshRuntimeGenerationGate _generation = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private DshRuntimeState _state = DshRuntimeState.NotInstalled;
    private DshRuntimeLease? _lease;
    private Uri? _readyUrl;
    private DshUpdateState _updateState = DshUpdateState.Idle;
    private DshUpdatePhase? _updatePhase;
    private string? _availableVersion;
    private IReadOnlyList<DshAvailableVersion> _availableVersions = DshUpdateCheckResult.EmptyVersions;
    private IReadOnlyList<DshAvailableVersion> _deferredVersions = DshUpdateCheckResult.EmptyVersions;
    private IReadOnlyList<DshAvailableVersion> _blockedVersions = DshUpdateCheckResult.EmptyVersions;
    private string? _requestedUpdateVersion;
    private string? _requestedRegistryOverride;
    private string? _catalogRegistryKey;
    private string? _operationRegistryKey;
    private long _operationIdentity;
    private long _publishedOperationIdentity;
    private string? _updateErrorCode;
    private string? _updateErrorClass;
    private string? _runtimeErrorClass;
    private CancellationTokenSource? _runCts;   // live lifecycle operation; guarded by _sync
    private Task? _operation;                   // single-flight lifecycle operation; guarded by _sync
    private DshOperationKind? _operationKind;   // active operation kind; guarded by _sync
    private bool _stopRequested;                // a Stop/Shutdown began and owns the terminal state; guarded by _sync
    private bool _disposed;

    public DshWebRuntimeSupervisor(
        DshWebRuntime runtime,
        IAgentBridgeService bridge,
        string logDirectory,
        string workspaceDirectory,
        PsxEnvironmentSettingsCoordinator settings)
    {
        _runtime = runtime;
        _bridge = bridge;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logDirectory = logDirectory;
        _workspaceDirectory = workspaceDirectory;
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(workspaceDirectory);
        _settings.DshRegistryChanged += OnDshRegistryChanged;
    }

    public DshRuntimeState State { get { lock (_sync) return _state; } }
    public Uri? ReadyUrl { get { lock (_sync) return _readyUrl; } }

    /// <summary>Wired by MainWindow: receives the ready URL so the
    /// WebViewHostPolicy frame whitelist can be updated. Null clears it.</summary>
    public Action<Uri?>? ReadyUrlChanged { get; set; }

    /// <summary>Wire value for the <c>dsh_runtime_status</c> state field.
    /// Enum <c>ToLowerInvariant</c> would emit <c>notinstalled</c>, which the
    /// shell does not recognize.</summary>
    public static string ToWireState(DshRuntimeState state) => state switch
    {
        DshRuntimeState.NotInstalled => "not_installed",
        DshRuntimeState.Installing => "installing",
        DshRuntimeState.Starting => "starting",
        DshRuntimeState.Ready => "ready",
        DshRuntimeState.Exited => "exited",
        DshRuntimeState.Failed => "failed",
        _ => "failed"
    };

    public static string ToWireUpdateState(DshUpdateState state) => state switch
    {
        DshUpdateState.Idle => "idle",
        DshUpdateState.Checking => "checking",
        DshUpdateState.UpToDate => "up_to_date",
        DshUpdateState.Available => "available",
        DshUpdateState.Updating => "updating",
        DshUpdateState.Failed => "failed",
        DshUpdateState.RequiresPsxUpdate => "requires_psx_update",
        _ => "failed"
    };

    public static string ToWireUpdatePhase(DshUpdatePhase phase) => phase switch
    {
        DshUpdatePhase.Downloading => "downloading",
        DshUpdatePhase.Validating => "validating",
        DshUpdatePhase.Restarting => "restarting",
        _ => "downloading"
    };

    /// <summary>
    /// Accept the loopback HTTP URL DSH prints at Ready. The path must be
    /// <c>/</c>. A missing query is allowed (test doubles and origin-only
    /// forms). A query, when present, must be exactly <c>?token=</c> plus a
    /// bounded token; the overlay WebView is the only caller that may open
    /// that URL. Fragments and userinfo are refused.
    /// </summary>
    public static bool TryAcceptReadyUrl(string? candidate, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaximumReadyUrlLength)
            return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(parsed.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return false;
        if (parsed.IsDefaultPort || parsed.Port is <= 0 or > 65535)
            return false;
        if (!string.Equals(parsed.AbsolutePath, "/", StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(parsed.Fragment))
            return false;

        var query = parsed.Query;
        if (string.IsNullOrEmpty(query))
        {
            url = parsed;
            return true;
        }

        if (!query.StartsWith("?token=", StringComparison.Ordinal) || query.Contains('&', StringComparison.Ordinal))
            return false;
        var token = query["?token=".Length..];
        if (!ReadyTokenPattern.IsMatch(token))
            return false;
        url = parsed;
        return true;
    }

    /// <summary>Authority form used by the DSH surface policy and loopback
    /// health checks (no path, query, or trailing slash). Never log the
    /// token-bearing URL — pass this origin to diagnostics instead.</summary>
    public static string? ToFrameOrigin(Uri? url)
    {
        if (url == null
            || !string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(url.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(url.UserInfo)
            || url.IsDefaultPort
            || url.Port is <= 0 or > 65535)
            return null;
        return url.GetLeftPart(UriPartial.Authority);
    }

    public void PrepareForStartup() => _runtime.PrepareForStartup();

    /// <summary>
    /// User-triggered install. Single-flight: concurrent callers join the
    /// in-flight run instead of queueing another full npm ci. The install
    /// honors the per-run cancellation token, so Stop/Shutdown interrupts it
    /// instead of blocking behind it.
    /// </summary>
    public Task InstallAndStartAsync() => BeginOperationAsync(DshOperationKind.Install);

    /// <summary>
    /// User-triggered retry: launch when installed, otherwise install. Shares
    /// the single operation slot with <see cref="InstallAndStartAsync"/> so
    /// retry bursts can never queue a second npm ci behind the first.
    /// </summary>
    public Task RetryAsync() => BeginOperationAsync(DshOperationKind.Retry);

    public Task CheckForUpdateAsync() => BeginOperationAsync(DshOperationKind.CheckUpdate);

    public Task RecheckWithRegistryAsync(string registryKey) =>
        BeginOperationAsync(DshOperationKind.Recheck, registryOverride: registryKey);

    public Task RetryInstallWithRegistryAsync(string registryKey) =>
        BeginOperationAsync(DshOperationKind.RetryInstall, registryOverride: registryKey);

    public Task UpdateAndRestartAsync(string? version = null) =>
        BeginOperationAsync(DshOperationKind.Update, version);

    /// <summary>
    /// Cancel only while the candidate is being downloaded or validated. The
    /// current server still owns dsh-current in those phases, so cancellation
    /// can discard dsh-next without interrupting the user's live session.
    /// Once the atomic switch begins, rollback owns recovery instead.
    /// </summary>
    public Task CancelUpdateAsync()
    {
        lock (_sync)
        {
            if (_disposed
                || _stopRequested
                || _operationKind != DshOperationKind.Update
                || _updateState != DshUpdateState.Updating
                || _updatePhase == DshUpdatePhase.Restarting)
            {
                return Task.CompletedTask;
            }

            _runCts?.Cancel();
        }
        return Task.CompletedTask;
    }

    private Task BeginOperationAsync(
        DshOperationKind kind,
        string? version = null,
        string? registryOverride = null)
    {
        lock (_sync)
        {
            if (_disposed || _stopRequested || _lifetimeCts.IsCancellationRequested)
                return Task.CompletedTask;
            if (_operation != null)
                return _operation;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _runCts = runCts;
            _operation = completion.Task;
            _operationKind = kind;
            _requestedUpdateVersion = kind == DshOperationKind.Update
                && !string.IsNullOrWhiteSpace(version)
                ? version.Trim()
                : null;
            _requestedRegistryOverride = kind is DshOperationKind.Recheck or DshOperationKind.RetryInstall
                && !string.IsNullOrWhiteSpace(registryOverride)
                ? registryOverride.Trim()
                : null;
            _ = ExecuteOperationAsync(completion, runCts, kind);
            return completion.Task;
        }
    }

    private async Task ExecuteOperationAsync(
        TaskCompletionSource completion,
        CancellationTokenSource runCts,
        DshOperationKind kind)
    {
        Exception? failure = null;
        try
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // A Stop that began while this operation was waiting owns the
                // terminal state: its CancelRun already hit this run token,
                // and StopHoldingLifecycleLock publishes Exited. Die quietly
                // instead of starting another npm ci.
                bool abandoned;
                lock (_sync)
                    abandoned = _stopRequested || runCts.IsCancellationRequested;
                if (!abandoned)
                {
                    try
                    {
                        switch (kind)
                        {
                            case DshOperationKind.Install:
                            case DshOperationKind.RetryInstall:
                                await InstallAndStartHoldingLockAsync(runCts, kind).ConfigureAwait(false);
                                break;
                            case DshOperationKind.Retry:
                                if (_runtime.CreateLaunchSpec() != null)
                                    await EnsureRunningHoldingLockAsync().ConfigureAwait(false);
                                else
                                    await InstallAndStartHoldingLockAsync(runCts, kind).ConfigureAwait(false);
                                break;
                            case DshOperationKind.CheckUpdate:
                            case DshOperationKind.Recheck:
                                await CheckForUpdateHoldingLockAsync(runCts.Token, kind).ConfigureAwait(false);
                                break;
                            case DshOperationKind.Update:
                                await UpdateAndRestartHoldingLockAsync(runCts).ConfigureAwait(false);
                                break;
                        }
                    }
                    catch (Exception ex) when (kind == DshOperationKind.Update)
                    {
                        await RecoverUnexpectedUpdateFailureHoldingLockAsync(ex).ConfigureAwait(false);
                    }
                }
            }
            finally { _lifecycleLock.Release(); }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            var shouldPublish = false;
            lock (_sync)
            {
                if (ReferenceEquals(_operation, completion.Task))
                {
                    _operation = null;
                    _operationKind = null;
                    _requestedUpdateVersion = null;
                    _requestedRegistryOverride = null;
                    if (_publishedOperationIdentity == _operationIdentity)
                    {
                        _operationRegistryKey = null;
                        shouldPublish = true;
                    }
                }
                if (ReferenceEquals(_runCts, runCts))
                    _runCts = null;
            }
            runCts.Dispose();
            if (shouldPublish)
                PublishCurrentState();
            if (failure == null)
                completion.TrySetResult();
            else
                completion.TrySetException(failure);
        }
    }

    public async Task EnsureRunningAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await EnsureRunningHoldingLockAsync().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>
    /// Cancels the running and any queued operation atomically, then stops the
    /// runtime. Never blocks synchronously on the lifecycle lock, so it is
    /// safe to call from the bridge dispatcher while an install is running.
    /// </summary>
    public async Task StopAsync()
    {
        RequestStop();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { StopHoldingLifecycleLock(); }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>
    /// Application shutdown: close the command entry (lifetime token), cancel
    /// the running and queued operations, and stop the runtime, bounding the
    /// lock wait so closing the window never hangs on npm.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _lifetimeCts.Cancel();
        RequestStop();
        var acquired = false;
        try { acquired = await _lifecycleLock.WaitAsync(ShutdownLockTimeout).ConfigureAwait(false); }
        catch (ObjectDisposedException) { acquired = false; }

        if (acquired)
        {
            try { StopHoldingLifecycleLock(); }
            finally { _lifecycleLock.Release(); }
        }
        else
        {
            // The install did not yield within the shutdown budget; tear down
            // anyway — the generation gate drops every stale publication.
            StopHoldingLifecycleLock();
        }
    }

    /// <summary>
    /// Atomically enter the stopping window: reject new operations, cancel the
    /// current run token (created before queuing, so it covers a queued
    /// operation too). <see cref="StopHoldingLifecycleLock"/> ends the window
    /// once the terminal Exited state is published, allowing later retries.
    /// </summary>
    private void RequestStop()
    {
        lock (_sync)
        {
            _stopRequested = true;
            _runCts?.Cancel();
        }
    }

    private async Task InstallAndStartHoldingLockAsync(
        CancellationTokenSource runCts,
        DshOperationKind kind)
    {
        if (kind == DshOperationKind.RetryInstall && _runtime.CreateLaunchSpec() != null)
            return;

        var snapshot = PrepareOperationSnapshot(kind, persistOverride: kind == DshOperationKind.RetryInstall);
        if (snapshot == null)
            return;

        RetireLiveLeaseHoldingLifecycleLock();

        if (_runtime.CreateLaunchSpec() != null)
        {
            await EnsureRunningHoldingLockAsync().ConfigureAwait(false);
            return;
        }

        ClearCatalogHoldingSync();
        SetOperationRegistry(snapshot.Key);
        SetState(DshRuntimeState.Installing);
        DshInstallResult result;
        try
        {
            result = await _runtime.InstallAsync(snapshot, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new DshInstallResult(false, null, null);
            runCts.Cancel();
        }

        if (!result.Success)
        {
            if (runCts.IsCancellationRequested)
                return;
            SetState(
                DshRuntimeState.Failed,
                result.ErrorCode ?? DshErrorClass.InstallFailed,
                result.ErrorClass ?? DshErrorClass.InstallFailed);
            return;
        }

        lock (_sync)
            _runtimeErrorClass = null;
        await EnsureRunningHoldingLockAsync().ConfigureAwait(false);
    }

    private async Task CheckForUpdateHoldingLockAsync(
        CancellationToken cancellationToken,
        DshOperationKind kind)
    {
        if (kind == DshOperationKind.Recheck && _runtime.CreateLaunchSpec() == null)
            return;

        var snapshot = PrepareOperationSnapshot(kind, persistOverride: kind == DshOperationKind.Recheck);
        if (snapshot == null)
            return;

        ClearCatalogHoldingSync();
        SetOperationRegistry(snapshot.Key);
        SetUpdateState(DshUpdateState.Checking);
        DshUpdateCheckResult result;
        try
        {
            result = await _runtime.CheckForUpdateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetUpdateState(DshUpdateState.Idle);
            return;
        }

        var currentKey = _settings.GetSnapshot().DshRegistry;
        lock (_sync)
        {
            if (!string.Equals(currentKey, snapshot.Key, StringComparison.Ordinal))
            {
                _availableVersions = DshUpdateCheckResult.EmptyVersions;
                _availableVersion = null;
                _deferredVersions = DshUpdateCheckResult.EmptyVersions;
                _blockedVersions = DshUpdateCheckResult.EmptyVersions;
                _catalogRegistryKey = null;
                _updateErrorCode = null;
                _updateErrorClass = null;
                _updateState = DshUpdateState.Idle;
            }
            else if (!result.Success)
            {
                _availableVersions = DshUpdateCheckResult.EmptyVersions;
                _availableVersion = null;
                _deferredVersions = DshUpdateCheckResult.EmptyVersions;
                _blockedVersions = DshUpdateCheckResult.EmptyVersions;
                _catalogRegistryKey = null;
                _updateErrorCode = result.ErrorCode;
                _updateErrorClass = result.ErrorClass;
                _updateState = DshUpdateState.Failed;
            }
            else
            {
                _availableVersions = result.AvailableVersions ?? DshUpdateCheckResult.EmptyVersions;
                _availableVersion = result.AvailableVersion;
                _deferredVersions = result.DeferredList;
                _blockedVersions = result.BlockedList;
                _catalogRegistryKey = snapshot.Key;
                _updateErrorCode = null;
                _updateErrorClass = null;
                _updateState = result.UpdateAvailable
                    ? DshUpdateState.Available
                    : _deferredVersions.Count > 0 || _blockedVersions.Count > 0
                        ? DshUpdateState.RequiresPsxUpdate
                        : DshUpdateState.UpToDate;
            }
        }
        PublishCurrentState();
    }

    private async Task UpdateAndRestartHoldingLockAsync(CancellationTokenSource runCts)
    {
        var snapshot = CurrentRegistry();
        string? requested;
        IReadOnlyList<DshAvailableVersion> allowlist;
        string? catalogKey;
        DshUpdateState stateOnEntry;
        lock (_sync)
        {
            requested = _requestedUpdateVersion;
            allowlist = _availableVersions;
            catalogKey = _catalogRegistryKey;
            stateOnEntry = _updateState;
        }

        if (!string.Equals(snapshot.Key, catalogKey, StringComparison.Ordinal))
        {
            ClearAllowlist();
            SetUpdateState(
                DshUpdateState.Failed,
                null,
                DshErrorClass.UpdateRegistryChanged);
            return;
        }

        if (stateOnEntry == DshUpdateState.RequiresPsxUpdate)
        {
            // Forged or racing update commands must not degrade this state
            // into a plain failure: nothing is installable in this PSX build
            // and the user needs a PSX update. Keep the state; surface a
            // transient hint through the same publish path.
            SetUpdateState(DshUpdateState.RequiresPsxUpdate, errorCode: DshErrorClass.UpdateRequiresPsx);
            return;
        }

        string? candidate;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!allowlist.Any(entry =>
                    string.Equals(entry.Version, requested, StringComparison.Ordinal)))
            {
                ClearAllowlist();
                SetUpdateState(
                    DshUpdateState.Failed,
                    null,
                    DshErrorClass.UpdateInvalidVersion);
                return;
            }
            candidate = requested;
        }
        else
        {
            lock (_sync)
                candidate = _updateState == DshUpdateState.Available ? _availableVersion : null;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                await CheckForUpdateHoldingLockAsync(runCts.Token, DshOperationKind.CheckUpdate)
                    .ConfigureAwait(false);
                lock (_sync)
                    candidate = _updateState == DshUpdateState.Available ? _availableVersion : null;
                if (string.IsNullOrWhiteSpace(candidate))
                    return;
                snapshot = CurrentRegistry();
                lock (_sync)
                    catalogKey = _catalogRegistryKey;
                if (!string.Equals(snapshot.Key, catalogKey, StringComparison.Ordinal))
                    return;
            }
        }

        SetOperationRegistry(snapshot.Key);
        SetUpdateState(DshUpdateState.Updating, candidate, phase: DshUpdatePhase.Downloading);
        DshUpdateResult staged;
        try
        {
            staged = await _runtime.StageUpdateAsync(
                snapshot,
                candidate,
                runCts.Token,
                () =>
                {
                    if (!runCts.IsCancellationRequested)
                        SetUpdateState(DshUpdateState.Updating, candidate, phase: DshUpdatePhase.Validating);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _runtime.DiscardStagedUpdate();
            CompleteCancelledUpdate(candidate);
            return;
        }
        if (!staged.Success)
        {
            if (runCts.IsCancellationRequested)
            {
                _runtime.DiscardStagedUpdate();
                CompleteCancelledUpdate(candidate);
                return;
            }
            _runtime.DiscardStagedUpdate();
            ClearAllowlist();
            SetUpdateState(
                DshUpdateState.Failed,
                null,
                staged.ErrorCode ?? DshErrorClass.UpdateFailed,
                errorClass: staged.ErrorClass);
            return;
        }

        // The candidate is fully staged before the live process is touched.
        // Cancellation is intentionally closed from here onward: rollback,
        // rather than user cancellation, owns every interrupted switch.
        SetUpdateState(DshUpdateState.Updating, candidate, phase: DshUpdatePhase.Restarting);
        if (runCts.IsCancellationRequested)
        {
            _runtime.DiscardStagedUpdate();
            CompleteCancelledUpdate(candidate);
            return;
        }
        // Wait for the old lease's bounded disposal so native .node handles
        // cannot keep dsh-current locked during the atomic directory swap.
        var retired = RetireLiveLeaseHoldingLifecycleLock();
        if (retired != null)
            await retired.DisposalTask.ConfigureAwait(false);
        if (runCts.IsCancellationRequested)
        {
            _runtime.DiscardStagedUpdate();
            SetUpdateState(DshUpdateState.Idle);
            return;
        }

        if (!_runtime.ApplyStagedUpdate(candidate))
        {
            _runtime.RollbackAppliedUpdate();
            ClearAllowlist();
            SetUpdateState(DshUpdateState.Failed, null, DshErrorClass.UpdateSwitchFailed);
            await EnsureRunningHoldingLockAsync().ConfigureAwait(false);
            return;
        }

        var spec = _runtime.CreateLaunchSpec();
        if (spec == null)
        {
            _runtime.RollbackAppliedUpdate();
            ClearAllowlist();
            SetUpdateState(DshUpdateState.Failed, null, DshErrorClass.UpdateRelaunchFailed);
            await EnsureRunningHoldingLockAsync().ConfigureAwait(false);
            return;
        }

        bool ready;
        try
        {
            ready = await StartProcessAsync(spec).WaitAsync(runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RetireLiveLeaseHoldingLifecycleLock();
            _runtime.RollbackAppliedUpdate();
            SetUpdateState(DshUpdateState.Idle);
            return;
        }

        if (ready)
        {
            _runtime.CommitAppliedUpdate();
            lock (_sync)
            {
                _availableVersions = DshUpdateCheckResult.EmptyVersions;
                _availableVersion = null;
                _deferredVersions = DshUpdateCheckResult.EmptyVersions;
                _blockedVersions = DshUpdateCheckResult.EmptyVersions;
                _catalogRegistryKey = null;
                _updateErrorCode = null;
                _updateState = DshUpdateState.UpToDate;
                _updatePhase = null;
            }
            PublishCurrentState();
            return;
        }

        // TryCommitProcessState already rolls back an uncommitted candidate
        // on Failed. Calling this again is harmless and covers a stale start.
        _runtime.RollbackAppliedUpdate();
        ClearAllowlist();
        SetUpdateState(DshUpdateState.Failed, null, DshErrorClass.UpdateRelaunchFailed);
        await StartRecoveredRuntimeHoldingLockAsync().ConfigureAwait(false);
    }

    private void CompleteCancelledUpdate(string candidate)
    {
        bool stopped;
        lock (_sync)
            stopped = _stopRequested || _lifetimeCts.IsCancellationRequested;
        // Cancellation during download/validate keeps the allowlist so the
        // user can confirm again without re-checking.
        SetUpdateState(stopped ? DshUpdateState.Idle : DshUpdateState.Available, candidate);
    }

    private async Task RecoverUnexpectedUpdateFailureHoldingLockAsync(Exception exception)
    {
        Log($"Unexpected DSH update failure ({exception.GetType().Name}): {exception}");
        var switched = _runtime.HasUncommittedUpdate;
        if (switched)
        {
            var retired = RetireLiveLeaseHoldingLifecycleLock();
            if (retired != null)
                await retired.DisposalTask.ConfigureAwait(false);
            _runtime.RollbackAppliedUpdate();
        }
        else
        {
            _runtime.DiscardStagedUpdate();
        }

        ClearAllowlist();
        SetUpdateState(
            DshUpdateState.Failed,
            null,
            DshErrorClass.UpdateFailed);

        bool needsStart;
        lock (_sync)
            needsStart = _lease == null || _readyUrl == null;
        if (needsStart)
            await StartRecoveredRuntimeHoldingLockAsync().ConfigureAwait(false);
    }

    private async Task StartRecoveredRuntimeHoldingLockAsync()
    {
        var spec = _runtime.CreateLaunchSpec();
        if (spec != null)
            await StartProcessAsync(spec).ConfigureAwait(false);
    }

    private async Task EnsureRunningHoldingLockAsync()
    {
        lock (_sync)
        {
            if (_state == DshRuntimeState.Ready && _readyUrl != null) return;
            if (_state == DshRuntimeState.Starting) return;
        }

        var spec = _runtime.CreateLaunchSpec();
        if (spec == null)
        {
            lock (_sync)
            {
                if (_state == DshRuntimeState.Failed)
                    return;
            }
            SetState(DshRuntimeState.NotInstalled);
            return;
        }

        _ = StartProcessAsync(spec);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void StopHoldingLifecycleLock()
    {
        DshRuntimeLease? lease;
        lock (_publishLock)
        {
            lock (_sync)
            {
                _generation.Invalidate();
                lease = _lease;
                _lease = null;
                _readyUrl = null;
                _state = DshRuntimeState.Exited;
            }

            lease?.TearDown();
            ReadyUrlChanged?.Invoke(null);
            PublishState(DshRuntimeState.Exited, null);
        }

        // End the stopping window: the terminal state is published, so later
        // install/retry commands may run again.
        lock (_sync)
            _stopRequested = false;
    }

    /// <summary>
    /// Drop a live process/job before swapping <c>dsh-current</c>. Does not
    /// publish Exited — the caller is about to publish Installing.
    /// </summary>
    private DshRuntimeLease? RetireLiveLeaseHoldingLifecycleLock()
    {
        DshRuntimeLease? lease;
        lock (_publishLock)
        {
            lock (_sync)
            {
                if (_lease == null)
                    return null;
                _generation.Invalidate();
                lease = _lease;
                _lease = null;
                _readyUrl = null;
            }

            lease?.TearDown();
            ReadyUrlChanged?.Invoke(null);
        }
        return lease;
    }

    private Task<bool> StartProcessAsync(DshLaunchSpec spec)
    {
        if (_lifetimeCts.IsCancellationRequested)
            return Task.FromResult(false);

        long generation;
        lock (_sync)
            generation = _generation.Begin();
        if (!TryCommitProcessState(generation, DshRuntimeState.Starting, null))
            return Task.FromResult(false);

        // The suspended-create + job-assign + resume sequence guarantees the
        // whole process tree joins the KILL_ON_JOB_CLOSE job before any child
        // can spawn. Failure details may contain absolute paths and stay in
        // the supervisor log; the wire only carries the fixed safe key.
        var launch = SuspendedJobProcessLauncher.TryStartInJob(
            spec.NodePath,
            // --no-open: the overlay WebView is the only document that opens
            // the Ready URL; a system-browser tab would consume the one-time
            // launch token and duplicate the surface.
            new[] { spec.EntryPath, "web", "--host", "127.0.0.1", "--port", "0", "--no-open" },
            _workspaceDirectory,
            Log);
        if (!launch.Succeeded || launch.Process == null)
        {
            Log($"DSH launch failed: {launch.FailureDetail}");
            TryCommitProcessState(
                generation,
                DshRuntimeState.Failed,
                null,
                DshErrorClass.LaunchFailed,
                DshErrorClass.LaunchFailed);
            return Task.FromResult(false);
        }

        var lease = new DshRuntimeLease(generation)
        {
            Process = launch.Process,
            Job = launch.Job,
            OutputReader = launch.Output,
            ErrorReader = launch.Error
        };
        var stale = false;
        lock (_sync)
        {
            if (!_generation.IsCurrent(generation))
                stale = true;
            else
            {
                _lease?.TearDown();
                _lease = lease;
                _readyUrl = null;
            }
        }
        if (stale)
        {
            lease.TearDown();
            lease.CompleteStartup(false);
            return lease.StartupTask;
        }

        lease.TrackTask(MonitorAsync(lease, generation));
        return lease.StartupTask;
    }

    private async Task MonitorAsync(DshRuntimeLease lease, long generation)
    {
        try
        {
            var process = lease.Process;
            if (process == null)
                return;

            var readyTcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exitedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => exitedTcs.TrySetResult(true);

            var output = lease.OutputReader;
            var error = lease.ErrorReader;
            if (output != null)
            {
                lease.TrackTask(Task.Run(async () =>
                {
                    try
                    {
                        string? line;
                        while ((line = await output.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            var match = ReadyPattern.Match(line);
                            if (match.Success && TryAcceptReadyUrl(match.Groups[1].Value, out var uri))
                                readyTcs.TrySetResult(uri);
                        }
                    }
                    catch { }
                    finally { readyTcs.TrySetResult(null); }
                }));
            }
            if (error != null)
            {
                lease.TrackTask(Task.Run(async () =>
                {
                    try
                    {
                        string? line;
                        while ((line = await error.ReadLineAsync().ConfigureAwait(false)) != null)
                            Log($"stderr: {line}");
                    }
                    catch { }
                }));
            }

            var winner = await Task.WhenAny(readyTcs.Task, exitedTcs.Task, Task.Delay(TimeSpan.FromSeconds(90)))
                .ConfigureAwait(false);

            if (winner == readyTcs.Task && await readyTcs.Task.ConfigureAwait(false) is { } url)
            {
                if (await HealthCheckAsync(url).ConfigureAwait(false)
                    && TryCommitProcessState(generation, DshRuntimeState.Ready, url))
                {
                    lease.CompleteStartup(true);
                    lease.TrackTask(WatchExitAsync(generation, exitedTcs));
                    return;
                }
            }

            if (lease.CancellationToken.IsCancellationRequested)
                return;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            TryCommitProcessState(
                generation,
                DshRuntimeState.Failed,
                null,
                ExitedQuietly(process) ? DshErrorClass.ProcessExited : DshErrorClass.StartTimeout,
                DshErrorClass.LaunchFailed);
        }
        finally
        {
            lease.CompleteStartup(false);
        }
    }

    private static bool ExitedQuietly(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private async Task WatchExitAsync(long generation, TaskCompletionSource<bool> exitedTcs)
    {
        try { await exitedTcs.Task.ConfigureAwait(false); }
        catch { return; }
        TryCommitProcessState(generation, DshRuntimeState.Exited, null);
    }

    /// <summary>
    /// Publish a process-owned state only when <paramref name="generation"/> is
    /// still the live start/stop epoch. The generation check, field update and
    /// origin/bridge publication share <c>_publishLock</c> with Stop so a
    /// superseded monitor cannot emit Ready after Exited.
    /// </summary>
    internal bool TryCommitProcessState(
        long generation,
        DshRuntimeState state,
        Uri? readyUrl,
        string? errorCode = null,
        string? errorClass = null)
    {
        lock (_publishLock)
        {
            DshRuntimeLease? retired = null;
            lock (_sync)
            {
                if (!_generation.IsCurrent(generation))
                    return false;
                if (state is DshRuntimeState.Exited or DshRuntimeState.Failed)
                {
                    retired = _lease;
                    _lease = null;
                    _readyUrl = null;
                }
                else
                {
                    _readyUrl = readyUrl;
                }

                _state = state;
                if (state == DshRuntimeState.Failed)
                    _runtimeErrorClass = errorClass ?? DshErrorClass.LaunchFailed;
                else if (state != DshRuntimeState.Installing)
                    _runtimeErrorClass = null;
            }

            retired?.TearDown();
            if (state == DshRuntimeState.Ready && _runtime.HasUncommittedUpdate)
                _runtime.CommitAppliedUpdate();
            else if ((state is DshRuntimeState.Failed or DshRuntimeState.Exited)
                     && _runtime.HasUncommittedUpdate)
                _runtime.RollbackAppliedUpdate();
            if (state == DshRuntimeState.Ready)
                ReadyUrlChanged?.Invoke(readyUrl);
            else if (state is DshRuntimeState.Failed or DshRuntimeState.Exited)
                ReadyUrlChanged?.Invoke(null);

            PublishState(state, errorCode, state == DshRuntimeState.Ready ? readyUrl : null);
            return true;
        }
    }

    internal static async Task<bool> HealthCheckAsync(Uri url)
    {
        var origin = ToFrameOrigin(url);
        if (origin == null)
            return false;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            // Probe the clean root only and never follow redirects: a 3xx to
            // the token URL would consume DSH's one-time launch token before
            // the overlay WebView can exchange it for the SameSite=Strict
            // session cookie.
            using var response = await client.GetAsync(origin + "/").ConfigureAwait(false);
            if ((int)response.StatusCode == 401)
                return true;
            if (!response.IsSuccessStatusCode)
                return false;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return body.Contains("__DSH_BOOT__", StringComparison.Ordinal)
                || body.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void SetState(DshRuntimeState state, string? errorCode = null, string? errorClass = null)
    {
        lock (_publishLock)
        {
            Uri? readyUrl;
            lock (_sync)
            {
                _state = state;
                readyUrl = _readyUrl;
                if (state == DshRuntimeState.Failed)
                    _runtimeErrorClass = errorClass;
                else if (state != DshRuntimeState.Installing)
                    _runtimeErrorClass = null;
            }

            if (state is DshRuntimeState.Failed or DshRuntimeState.Exited)
                ReadyUrlChanged?.Invoke(null);

            PublishState(state, errorCode, readyUrl);
        }
    }

    private void ClearAllowlist()
    {
        lock (_sync)
        {
            _availableVersions = DshUpdateCheckResult.EmptyVersions;
            _availableVersion = null;
            _deferredVersions = DshUpdateCheckResult.EmptyVersions;
            _blockedVersions = DshUpdateCheckResult.EmptyVersions;
            _catalogRegistryKey = null;
        }
    }

    private void ClearCatalogHoldingSync()
    {
        lock (_sync)
        {
            _availableVersions = DshUpdateCheckResult.EmptyVersions;
            _availableVersion = null;
            _deferredVersions = DshUpdateCheckResult.EmptyVersions;
            _blockedVersions = DshUpdateCheckResult.EmptyVersions;
            _catalogRegistryKey = null;
            _updateErrorCode = null;
            _updateErrorClass = null;
        }
    }

    private void SetUpdateState(
        DshUpdateState state,
        string? availableVersion = null,
        string? errorCode = null,
        DshUpdatePhase? phase = null,
        string? errorClass = null)
    {
        lock (_sync)
        {
            _updateState = state;
            _updatePhase = state == DshUpdateState.Updating ? phase : null;
            if (availableVersion != null)
                _availableVersion = availableVersion;
            _updateErrorCode = errorCode;
            _updateErrorClass = state == DshUpdateState.Failed ? errorClass : null;
        }
        PublishCurrentState();
    }

    private void PublishCurrentState()
    {
        lock (_publishLock)
        {
            DshRuntimeState state;
            Uri? readyUrl;
            lock (_sync)
            {
                state = _state;
                readyUrl = _readyUrl;
            }
            PublishState(state, null, readyUrl);
        }
    }

    private void PublishState(DshRuntimeState state, string? errorCode, Uri? readyUrl = null)
    {
        if (state == DshRuntimeState.Ready && readyUrl == null)
        {
            lock (_sync)
                readyUrl = _readyUrl;
        }

        string updateState;
        string? updatePhase;
        string? availableVersion;
        object[] availableVersions;
        object[] deferredVersions;
        object[] blockedVersions;
        string? updateErrorCode;
        string? catalogRegistryKey;
        string? operationRegistryKey;
        string? registryKey;
        string? runtimeErrorClass;
        string? updateErrorClass;
        lock (_sync)
        {
            updateState = ToWireUpdateState(_updateState);
            updatePhase = _updateState == DshUpdateState.Updating && _updatePhase.HasValue
                ? ToWireUpdatePhase(_updatePhase.Value)
                : null;
            availableVersion = _availableVersion;
            availableVersions = _availableVersions
                .Select(entry => (object)new
                {
                    version = entry.Version,
                    tags = entry.Tags.ToArray()
                })
                .ToArray();
            deferredVersions = _deferredVersions
                .Select(entry => (object)new
                {
                    version = entry.Version,
                    tags = entry.Tags.ToArray()
                })
                .ToArray();
            blockedVersions = _blockedVersions
                .Select(entry => (object)new
                {
                    version = entry.Version,
                    tags = entry.Tags.ToArray()
                })
                .ToArray();
            updateErrorCode = _updateState is DshUpdateState.Failed or DshUpdateState.RequiresPsxUpdate
                ? _updateErrorCode
                : null;
            catalogRegistryKey = _catalogRegistryKey;
            operationRegistryKey = _operationRegistryKey;
            runtimeErrorClass = state == DshRuntimeState.Failed ? _runtimeErrorClass : null;
            updateErrorClass = _updateState == DshUpdateState.Failed ? _updateErrorClass : null;
        }
        registryKey = _settings.GetSnapshot().DshRegistry;
        // The wire `errorClass` is a fixed DshErrorClass code the frontend
        // maps to copy; composed sentences never cross the bridge.
        var wireErrorCode = state == DshRuntimeState.Failed
            ? (errorCode ?? DshErrorClass.RuntimeUnavailable)
            : null;
        _ = _bridge.SendEventAsync(new
        {
            type = "dsh_runtime_status",
            state = ToWireState(state),
            readyUrl = state == DshRuntimeState.Ready ? ToFrameOrigin(readyUrl) : null,
            errorClass = wireErrorCode,
            currentVersion = _runtime.CurrentVersion,
            updateState,
            updatePhase,
            availableVersion,
            availableVersions,
            deferredVersions,
            blockedVersions,
            updateErrorCode,
            registryKey,
            operationRegistryKey,
            catalogRegistryKey,
            runtimeErrorClass,
            updateErrorClass
        });
    }

    private DshRegistryDescriptor? PrepareOperationSnapshot(DshOperationKind kind, bool persistOverride)
    {
        if (persistOverride)
        {
            string? requested;
            lock (_sync)
                requested = _requestedRegistryOverride;
            if (string.IsNullOrWhiteSpace(requested) || !DshRegistryDescriptor.TryGet(requested, out _))
                return CurrentRegistry();

            var result = _settings.SetDshRegistry(requested);
            if (!result.Success)
            {
                FailSettingsWrite(kind, result);
                return null;
            }

            if (result.Changed)
                _ = _settings.PublishBroadcastAsync();
            return result.Snapshot.Registry;
        }

        return CurrentRegistry();
    }

    private void FailSettingsWrite(DshOperationKind kind, PsxEnvironmentSetResult result)
    {
        var errorClass = result.ErrorClass ?? DshErrorClass.SettingsWriteFailed;
        if (kind is DshOperationKind.RetryInstall)
            SetState(DshRuntimeState.Failed, DshErrorClass.SettingsWriteFailed, errorClass);
        else
            SetUpdateState(DshUpdateState.Failed, errorCode: DshErrorClass.SettingsWriteFailed, errorClass: errorClass);
    }

    private DshRegistryDescriptor CurrentRegistry() =>
        DshRegistryDescriptor.ResolveStoredOrOfficial(_settings.GetSnapshot().DshRegistry);

    private void SetOperationRegistry(string key)
    {
        lock (_sync)
        {
            _operationIdentity++;
            _publishedOperationIdentity = _operationIdentity;
            _operationRegistryKey = key;
        }
        PublishCurrentState();
    }

    private void OnDshRegistryChanged(object? sender, PsxEnvironmentSettingsSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_operation != null)
                return;
            _availableVersions = DshUpdateCheckResult.EmptyVersions;
            _availableVersion = null;
            _deferredVersions = DshUpdateCheckResult.EmptyVersions;
            _blockedVersions = DshUpdateCheckResult.EmptyVersions;
            _catalogRegistryKey = null;
            _updateErrorCode = null;
            _updateErrorClass = null;
            _updateState = DshUpdateState.Idle;
            _updatePhase = null;
        }
        PublishCurrentState();
    }

    private void Log(string message) =>
        RotatingDiagnosticLog.AppendLine(Path.Combine(_logDirectory, "dsh-supervisor.log"), message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.DshRegistryChanged -= OnDshRegistryChanged;

        _lifetimeCts.Cancel();
        RequestStop();
        if (_lifecycleLock.Wait(0))
        {
            try { StopHoldingLifecycleLock(); }
            finally { _lifecycleLock.Release(); }
        }
        else
        {
            StopHoldingLifecycleLock();
        }

        _lifetimeCts.Dispose();
        _lifecycleLock.Dispose();
    }
}

/// <summary>
/// Monotonic start/stop epoch. Each launched process owns one token; Stop and
/// a newer Start invalidate every older token so stale monitors cannot publish.
/// </summary>
internal sealed class DshRuntimeGenerationGate
{
    private long _generation;

    public long Current => Interlocked.Read(ref _generation);

    public long Begin() => Interlocked.Increment(ref _generation);

    public long Invalidate() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(long generation) => Interlocked.Read(ref _generation) == generation;
}

/// <summary>
/// One start epoch's process tree, Job Object, stream readers and monitor
/// tasks. Stop and a failed/exited commit tear the lease down; a newer start
/// never inherits the previous job. TearDown is two-phase: the synchronous
/// phase (safe to run under the supervisor's publish lock) only marks the
/// lease torn down, cancels its token and kills the tree — it never waits on
/// the tracked tasks, because the task currently executing the teardown chain
/// (MonitorAsync / WatchExitAsync → TryCommitProcessState → TearDown) is
/// itself one of them. The disposal phase runs on the thread pool: it awaits
/// the remaining tasks (bounded), then disposes the readers, Process and job
/// so repeated restarts leak no handles.
/// </summary>
internal sealed class DshRuntimeLease
{
    private static readonly TimeSpan TeardownTaskTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly List<Task> _tasks = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _startup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Captured at construction: reading the token stays safe after the CTS is
    // disposed by the disposal phase.
    private readonly CancellationToken _token;
    private bool _tornDown;

    public DshRuntimeLease(long generation)
    {
        Generation = generation;
        _token = _cts.Token;
    }

    public long Generation { get; }
    public Process? Process { get; set; }
    public JobObjectHandle? Job { get; set; }
    public StreamReader? OutputReader { get; set; }
    public StreamReader? ErrorReader { get; set; }

    public CancellationToken CancellationToken => _token;
    public Task<bool> StartupTask => _startup.Task;
    public Task DisposalTask => _disposed.Task;

    public void CompleteStartup(bool ready) => _startup.TrySetResult(ready);

    public void TrackTask(Task task)
    {
        lock (_sync)
        {
            if (_tornDown)
                return;
            _tasks.Add(task);
        }
    }

    public void TearDown()
    {
        Task[] tasks;
        lock (_sync)
        {
            if (_tornDown)
                return;
            _tornDown = true;
            tasks = _tasks.ToArray();
        }

        try { _cts.Cancel(); } catch { }
        _startup.TrySetResult(false);

        var process = Process;
        Process = null;
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }

        var output = OutputReader;
        var error = ErrorReader;
        OutputReader = null;
        ErrorReader = null;
        var job = Job;
        Job = null;

        // Disposal phase: a separate pool task awaits the tracked tasks
        // (including the one that called TearDown — it completes right after
        // this returns), then closes the streams which force any wedged
        // reader loops to end.
        _ = Task.Run(async () =>
        {
            try
            {
                try { await Task.WhenAll(tasks).WaitAsync(TeardownTaskTimeout).ConfigureAwait(false); }
                catch { }

                try { output?.Dispose(); } catch { }
                try { error?.Dispose(); } catch { }
                if (process != null)
                {
                    try { process.WaitForExit(2000); } catch { }
                    try { process.Dispose(); } catch { }
                }
                try { job?.Dispose(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
            finally { _disposed.TrySetResult(); }
        });
    }
}
