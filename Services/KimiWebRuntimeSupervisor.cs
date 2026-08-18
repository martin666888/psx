using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

public enum KimiWebRuntimeState
{
    Unavailable,
    Stopped,
    Starting,
    Ready,
    Stopping,
    Failed,
    Exited
}

/// <summary>
/// Fixed wire-safe error keys for the <c>kimi_web_runtime_status</c>
/// <c>errorClass</c> field; the frontend maps them to copy. Failure details,
/// paths and stdout/stderr never cross the bridge.
/// </summary>
internal enum KimiWebErrorClass
{
    LaunchFailed,
    StartTimeout,
    HealthCheckFailed
}

/// <summary>
/// Process-level singleton supervising the embedded <c>kimi web</c> server:
/// launch with a Job Object (KILL_ON_JOB_CLOSE), parse the ready URL from the
/// startup banner, health-check, and broadcast kimi_web_runtime_status.
/// Workspace ≠ process: closing the tab never stops the server.
///
/// The ready URL carries a <c>#token=</c> fragment. The token is kept in
/// memory only, used solely for loopback Authorization headers (meta /
/// shutdown), and is never logged or persisted; stop / restart / failed /
/// exited clear it immediately. Logs record the origin only.
/// </summary>
public sealed class KimiWebRuntimeSupervisor : IDisposable
{
    /// <summary>
    /// Matches the startup banner line that carries the loopback URL
    /// (including its #token= fragment), e.g.
    /// <c>  Local:    http://127.0.0.1:49910/#token=...</c>.
    /// </summary>
    private static readonly Regex ReadyPattern = new(
        @"Local:\s+(https?://[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReadyTokenPattern = new(
        @"^[A-Za-z0-9_-]{16,512}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int MaximumReadyUrlLength = 2048;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShutdownRequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GracefulExitWait = TimeSpan.FromSeconds(5);

    /// <summary>Bounded wait for the lifecycle lock during application
    /// shutdown; after it elapses the teardown runs without the lock instead
    /// of blocking the window close.</summary>
    private static readonly TimeSpan ShutdownLockTimeout = TimeSpan.FromSeconds(10);

    private readonly KimiCodeAcpRuntime _runtime;
    private readonly IAgentBridgeService _bridge;
    private readonly string _logDirectory;
    private readonly string _workspaceDirectory;
    private readonly object _sync = new();
    private readonly object _publishLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly KimiWebRuntimeGenerationGate _generation = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private KimiWebRuntimeState _state = KimiWebRuntimeState.Stopped;
    private KimiWebRuntimeLease? _lease;
    private Uri? _readyUrl;   // includes the #token= fragment; wire-ready
    private string? _token;   // in-memory only; never logged or persisted
    private Task? _operation; // single-flight lifecycle operation; guarded by _sync
    private Task _cleanupTask = Task.CompletedTask; // every retired lease; guarded by _sync
    private bool _stopRequested; // a Stop/Shutdown began and owns the terminal state; guarded by _sync
    private bool _disposed;

    public KimiWebRuntimeSupervisor(
        KimiCodeAcpRuntime runtime,
        IAgentBridgeService bridge,
        string logDirectory,
        string workspaceDirectory)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _logDirectory = logDirectory;
        _workspaceDirectory = workspaceDirectory;
        Directory.CreateDirectory(logDirectory);
        // Neutral working directory for `kimi web` sessions (the server falls
        // back to process.cwd()); never an internal PSX tree or a user's
        // project directories.
        Directory.CreateDirectory(workspaceDirectory);
    }

    public KimiWebRuntimeState State { get { lock (_sync) return _state; } }

    /// <summary>Full ready URL including the #token= fragment; set only in the
    /// Ready state. Consumed by the status broadcast, iframe src, export and
    /// shutdown paths — never forwarded to the frame policy chain.</summary>
    public Uri? ReadyUrl { get { lock (_sync) return _readyUrl; } }

    /// <summary>
    /// Token-bearing ready snapshot for host-mediated loopback requests (the
    /// export download). The token stays in memory and is used only for the
    /// loopback Authorization header; it never leaves the process, crosses
    /// the bridge, or is logged or persisted.
    /// </summary>
    internal (Uri ReadyUrl, string Token)? GetReadyTokenSnapshot()
    {
        lock (_sync)
        {
            if (_readyUrl == null || string.IsNullOrEmpty(_token))
                return null;
            return (_readyUrl, _token);
        }
    }

    /// <summary>Wired by MainWindow: receives the ready origin (fragment
    /// stripped) for the frame whitelist. Null clears it.</summary>
    public event Action<string?>? FrameOriginChanged;

    /// <summary>
    /// Optional host hook that prepares the exact Kimi frame origin before a
    /// Ready state is committed. MainWindow wires this to WebViewHostPolicy's
    /// document-start registration. The supervisor awaits it while holding the
    /// lifecycle gate, so the iframe can never navigate before the export hook
    /// is installed; Stop waits for the same gate and then clears the slot.
    /// </summary>
    public Func<string, Task>? FrameOriginPreparingAsync { get; set; }

    /// <summary>Wire value for the <c>kimi_web_runtime_status</c> state field.</summary>
    public static string ToWireState(KimiWebRuntimeState state) => state switch
    {
        KimiWebRuntimeState.Unavailable => "unavailable",
        KimiWebRuntimeState.Stopped => "stopped",
        KimiWebRuntimeState.Starting => "starting",
        KimiWebRuntimeState.Ready => "ready",
        KimiWebRuntimeState.Stopping => "stopping",
        KimiWebRuntimeState.Failed => "failed",
        KimiWebRuntimeState.Exited => "exited",
        _ => "failed"
    };

    /// <summary>Accept only the loopback HTTP origin `kimi web` is allowed to
    /// bind; the fragment (token) is preserved.</summary>
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
        if (!string.IsNullOrEmpty(parsed.Query))
            return false;

        var fragment = parsed.Fragment;
        if (!fragment.StartsWith("#token=", StringComparison.Ordinal)
            || fragment.IndexOf('&') >= 0)
            return false;
        var token = fragment["#token=".Length..];
        if (!ReadyTokenPattern.IsMatch(token))
            return false;
        url = parsed;
        return true;
    }

    /// <summary>Origin form used for the frame whitelist and loopback HTTP
    /// calls (no fragment, no trailing slash).</summary>
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

    /// <summary>User-triggered launch (create / focus). Single-flight:
    /// concurrent callers join the in-flight run.</summary>
    public Task StartAsync() => BeginOperationAsync();

    /// <summary>User-triggered retry: re-resolves the launch spec; when still
    /// unavailable it only re-publishes the reason (no process, no download).</summary>
    public Task RetryAsync() => BeginOperationAsync();

    public async Task StopAsync()
    {
        RequestStop();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await StopHoldingLifecycleLockAsync().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    /// <summary>
    /// Application shutdown: cancel the lifetime token, close the command
    /// entry, and stop the runtime, bounding the lock wait so closing the
    /// window never hangs.
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
            try { await StopHoldingLifecycleLockAsync().ConfigureAwait(false); }
            finally { _lifecycleLock.Release(); }
        }
        else
        {
            // The running operation did not yield within the shutdown budget;
            // tear down anyway — the generation gate drops stale publications.
            await StopHoldingLifecycleLockAsync().ConfigureAwait(false);
        }
    }

    private Task BeginOperationAsync()
    {
        lock (_sync)
        {
            if (_disposed || _stopRequested || _lifetimeCts.IsCancellationRequested)
                return Task.CompletedTask;
            if (_operation != null)
                return _operation;

            // The placeholder task is published before the body runs so even
            // a synchronously completing run cannot leave a stale entry that
            // a later caller would mistake for a live one.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = completion.Task;
            _ = ExecuteOperationAsync(completion);
            return completion.Task;
        }
    }

    private async Task ExecuteOperationAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                bool abandoned;
                lock (_sync)
                    abandoned = _stopRequested || _lifetimeCts.IsCancellationRequested;
                if (!abandoned)
                    await EnsureRunningHoldingLockAsync().ConfigureAwait(false);
            }
            finally { _lifecycleLock.Release(); }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_operation, completion.Task))
                    _operation = null;
            }
            if (failure == null)
                completion.TrySetResult();
            else
                completion.TrySetException(failure);
        }
    }

    /// <summary>
    /// Launch when not already running. A null launch spec becomes the
    /// unavailable state with the wire-safe reason; retry shares this path —
    /// there is no install step, so retry only re-validates the spec.
    /// </summary>
    private async Task EnsureRunningHoldingLockAsync()
    {
        lock (_sync)
        {
            if (_state == KimiWebRuntimeState.Ready && _readyUrl != null) return;
            if (_state == KimiWebRuntimeState.Starting) return;
        }

        // A failed/exited/explicitly-stopped generation may still be draining
        // redirected pipes and releasing its Job Object. Never overlap a new
        // Kimi process with that bounded cleanup window: both generations use
        // the same credential store and session/database files.
        await AwaitTrackedCleanupAsync().ConfigureAwait(false);

        var spec = _runtime.TryCreateWebLaunchSpec(_workspaceDirectory, out var reason);
        if (spec == null)
        {
            PublishUnavailable(reason);
            return;
        }
        _ = StartProcessAsync(spec);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Task<bool> StartProcessAsync(KimiWebLaunchSpec spec)
    {
        if (_lifetimeCts.IsCancellationRequested)
            return Task.FromResult(false);

        long generation;
        lock (_sync)
            generation = _generation.Begin();
        if (!TryCommitProcessState(generation, KimiWebRuntimeState.Starting, null))
            return Task.FromResult(false);

        // The suspended-create + job-assign + resume sequence guarantees the
        // whole process tree joins the KILL_ON_JOB_CLOSE job before any child
        // can spawn. Failure details may contain absolute paths and stay in
        // the supervisor log; the wire only carries the fixed safe key.
        var launch = SuspendedJobProcessLauncher.TryStartInJob(
            spec.NodePath,
            new[] { spec.EntryPath, "web", "--host", "127.0.0.1", "--port", "0", "--no-open" },
            spec.WorkingDirectory,
            Log,
            spec.Environment);
        if (!launch.Succeeded || launch.Process == null)
        {
            Log($"Kimi web launch failed: {launch.FailureDetail}");
            TryCommitProcessState(generation, KimiWebRuntimeState.Failed, null, KimiWebErrorClass.LaunchFailed);
            return Task.FromResult(false);
        }

        var lease = new KimiWebRuntimeLease(generation)
        {
            Process = launch.Process,
            Job = launch.Job,
            OutputReader = launch.Output,
            ErrorReader = launch.Error
        };
        var stale = false;
        KimiWebRuntimeLease? displaced = null;
        lock (_sync)
        {
            if (!_generation.IsCurrent(generation))
                stale = true;
            else
            {
                displaced = _lease;
                _lease = lease;
                _readyUrl = null;
                _token = null;
            }
        }
        if (displaced != null)
        {
            displaced.TearDown();
            TrackCleanup(displaced.DisposalTask);
        }
        if (stale)
        {
            lease.TearDown();
            TrackCleanup(lease.DisposalTask);
            lease.CompleteStartup(false);
            return lease.StartupTask;
        }

        // Monitor/exit-watch tasks do not own process resources and can be the
        // task that initiates teardown. Keeping them out of the lease's reader
        // wait set avoids a guaranteed self-wait timeout on failed/exited
        // generations; the generation gate still drops every stale commit.
        _ = MonitorAsync(lease, generation);
        return lease.StartupTask;
    }

    private async Task MonitorAsync(KimiWebRuntimeLease lease, long generation)
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
            if (lease.ErrorReader != null)
            {
                lease.TrackTask(Task.Run(async () =>
                {
                    try
                    {
                        // Drain the pipe so the child cannot block, but never
                        // persist raw provider output. stderr may contain a
                        // token, user paths or session content in current or
                        // future Kimi versions; diagnostics use fixed safe
                        // categories elsewhere in this supervisor.
                        while (await lease.ErrorReader.ReadLineAsync().ConfigureAwait(false) != null) { }
                    }
                    catch { }
                }));
            }

            var winner = await Task.WhenAny(readyTcs.Task, exitedTcs.Task, Task.Delay(StartTimeout))
                .ConfigureAwait(false);

            if (winner == readyTcs.Task && await readyTcs.Task.ConfigureAwait(false) is { } url)
            {
                var verified = await VerifyReadyAsync(url).ConfigureAwait(false);
                if (verified.Ok && await TryCommitReadyStateAsync(generation, url).ConfigureAwait(false))
                {
                    // The URL fragment carries the token; never log it. Only
                    // the origin is recorded for diagnostics.
                    Log($"Kimi web ready at {ToFrameOrigin(url)}"
                        + (string.IsNullOrEmpty(verified.ServerVersion)
                            ? ""
                            : $", server_version={verified.ServerVersion}"));
                    lease.CompleteStartup(true);
                    _ = WatchExitAsync(generation, exitedTcs);
                    return;
                }
                Log($"Kimi web health check failed at {ToFrameOrigin(url)}; stopping.");
                TryCommitProcessState(generation, KimiWebRuntimeState.Failed, null, KimiWebErrorClass.HealthCheckFailed);
                return;
            }

            if (lease.CancellationToken.IsCancellationRequested)
                return;
            TryCommitProcessState(
                generation,
                KimiWebRuntimeState.Failed,
                null,
                ExitedQuietly(process) ? KimiWebErrorClass.LaunchFailed : KimiWebErrorClass.StartTimeout);
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
        TryCommitProcessState(generation, KimiWebRuntimeState.Exited, null);
    }

    /// <summary>
    /// Complete the WebView document-start registration before exposing the
    /// token-bearing Ready URL. The lifecycle gate serializes this handshake
    /// with Stop/Shutdown; a stop requested while registration is in flight
    /// prevents the Ready commit and clears the prepared script immediately.
    /// </summary>
    private async Task<bool> TryCommitReadyStateAsync(long generation, Uri readyUrl)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!_generation.IsCurrent(generation)
                    || _stopRequested
                    || _lifetimeCts.IsCancellationRequested)
                    return false;
            }

            var origin = ToFrameOrigin(readyUrl);
            if (origin == null)
                return false;

            var prepare = FrameOriginPreparingAsync;
            if (prepare != null)
            {
                try
                {
                    await prepare(origin).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"Kimi web frame preparation failed: {ex.GetType().Name}.");
                    return false;
                }
            }

            lock (_sync)
            {
                if (!_generation.IsCurrent(generation)
                    || _stopRequested
                    || _lifetimeCts.IsCancellationRequested)
                    return false;
            }

            return TryCommitProcessState(generation, KimiWebRuntimeState.Ready, readyUrl);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Publish a process-owned state only when <paramref name="generation"/> is
    /// still the live start/stop epoch. The generation check, field update and
    /// origin/bridge publication share <c>_publishLock</c> with Stop so a
    /// superseded monitor cannot emit Ready after Stopped.
    /// </summary>
    internal bool TryCommitProcessState(
        long generation,
        KimiWebRuntimeState state,
        Uri? readyUrl,
        KimiWebErrorClass? error = null)
    {
        lock (_publishLock)
        {
            KimiWebRuntimeLease? retired = null;
            lock (_sync)
            {
                // RequestStop sets this flag before waiting for the lifecycle
                // gate. Reject monitor publications during that window too,
                // so an in-flight frame preparation cannot flash Failed/Ready
                // immediately before the authoritative Stopping state.
                if (!_generation.IsCurrent(generation) || _stopRequested)
                    return false;
                if (state is KimiWebRuntimeState.Failed or KimiWebRuntimeState.Exited)
                {
                    retired = _lease;
                    _lease = null;
                    _readyUrl = null;
                    _token = null;
                }
                else
                {
                    _readyUrl = readyUrl;
                    _token = state == KimiWebRuntimeState.Ready ? ExtractToken(readyUrl!) : null;
                }
                _state = state;
            }

            if (retired != null)
            {
                retired.TearDown();
                TrackCleanup(retired.DisposalTask);
            }
            if (state == KimiWebRuntimeState.Ready)
                FrameOriginChanged?.Invoke(ToFrameOrigin(readyUrl));
            else if (state is KimiWebRuntimeState.Failed or KimiWebRuntimeState.Exited)
                FrameOriginChanged?.Invoke(null);

            PublishState(state, error);
            return true;
        }
    }

    /// <summary>
    /// Health gate for the announced ready URL: the unauthenticated
    /// <c>/api/v1/healthz</c> must answer 200 and the token-bearing
    /// <c>/api/v1/meta</c> must confirm (a token that does not authenticate
    /// means the embedded app cannot function, so readiness is refused).
    /// </summary>
    private static async Task<(bool Ok, string? ServerVersion)> VerifyReadyAsync(Uri url)
    {
        var origin = ToFrameOrigin(url);
        if (origin == null)
            return (false, null);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using (var healthz = await client.GetAsync(origin + "/api/v1/healthz").ConfigureAwait(false))
            {
                if (!healthz.IsSuccessStatusCode)
                    return (false, null);
            }

            var token = ExtractToken(url);
            if (string.IsNullOrEmpty(token))
                return (false, null);

            using var request = new HttpRequestMessage(HttpMethod.Get, origin + "/api/v1/meta");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var meta = await client.SendAsync(request).ConfigureAwait(false);
            if (!meta.IsSuccessStatusCode)
                return (false, null);
            var body = await meta.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (true, ParseServerVersion(body));
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Best-effort <c>server_version</c> read for the diagnostic log
    /// only; a parse failure never blocks readiness.</summary>
    private static string? ParseServerVersion(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("server_version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Read the token from the ready URL's <c>#token=</c> fragment.
    /// The token lives in memory only and is never logged or persisted.</summary>
    private static string? ExtractToken(Uri url)
    {
        var fragment = url.Fragment;
        if (!fragment.StartsWith("#token=", StringComparison.Ordinal)
            || fragment.IndexOf('&') >= 0)
            return null;
        var token = fragment["#token=".Length..];
        return ReadyTokenPattern.IsMatch(token) ? token : null;
    }

    /// <summary>Graceful stop: ask the server to shut down (Bearer, in-memory
    /// token) and wait briefly for it to exit. False means the kill fallback
    /// must be used.</summary>
    private static async Task<bool> TryGracefulShutdownAsync(Uri readyUrl, string token, Process process)
    {
        var origin = ToFrameOrigin(readyUrl);
        if (origin == null)
            return false;
        try
        {
            using var client = new HttpClient { Timeout = ShutdownRequestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/api/v1/shutdown");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            using var exitCts = new CancellationTokenSource(GracefulExitWait);
            try
            {
                // WaitForExitAsync completes when the process exits; the token
                // bounds the wait (Process.WaitForExitAsync returns Task, so
                // reaching this point means the process is gone).
                await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (InvalidOperationException) { return ExitedQuietly(process); }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Atomically enter the stopping window: reject new operations.
    /// <see cref="StopHoldingLifecycleLockAsync"/> ends the window once the
    /// terminal Stopped state is published, allowing later retries.</summary>
    private void RequestStop()
    {
        lock (_sync)
            _stopRequested = true;
    }

    /// <summary>
    /// Stop the runtime: capture and clear the ready URL/token immediately,
    /// publish the transient Stopping state, attempt a graceful shutdown with
    /// the captured token, then tear the process tree down (kill fallback when
    /// the graceful stop failed or timed out) and publish the terminal Stopped
    /// state. The frame origin is revoked before Stopping is published.
    /// </summary>
    private async Task StopHoldingLifecycleLockAsync()
    {
        KimiWebRuntimeLease? lease;
        string? token;
        Uri? readyUrl;
        lock (_publishLock)
        {
            lock (_sync)
            {
                _generation.Invalidate();
                lease = _lease;
                _lease = null;
                readyUrl = _readyUrl;
                token = _token;
                _readyUrl = null;
                _token = null;
                _state = KimiWebRuntimeState.Stopping;
            }
            FrameOriginChanged?.Invoke(null);
            PublishState(KimiWebRuntimeState.Stopping);
        }

        var process = lease?.Process;
        if (process != null && !ExitedQuietly(process) && token != null && readyUrl != null)
        {
            var exited = await TryGracefulShutdownAsync(readyUrl, token, process).ConfigureAwait(false);
            Log(exited
                ? "Kimi web shut down gracefully after the /api/v1/shutdown request."
                : "Kimi web did not exit after /api/v1/shutdown; killing the process tree.");
        }

        // TearDown kills the tree when the process is still alive. Its
        // disposal phase is internally bounded; await it before advertising
        // Stopped so an immediate Retry cannot overlap the old process, pipe
        // readers or Job Object with the new generation.
        if (lease != null)
        {
            lease.TearDown();
            TrackCleanup(lease.DisposalTask);
        }
        await AwaitTrackedCleanupAsync().ConfigureAwait(false);

        lock (_publishLock)
        {
            lock (_sync)
                _state = KimiWebRuntimeState.Stopped;
            PublishState(KimiWebRuntimeState.Stopped);
        }
        lock (_sync)
            _stopRequested = false;
    }

    /// <summary>Publish the unavailable state (launch spec could not be
    /// resolved) with the wire-safe reason; no process is touched.</summary>
    private void PublishUnavailable(KimiWebLaunchUnavailableReason reason)
    {
        lock (_publishLock)
        {
            lock (_sync)
            {
                _generation.Invalidate();
                _state = KimiWebRuntimeState.Unavailable;
                _readyUrl = null;
                _token = null;
            }
            FrameOriginChanged?.Invoke(null);
            PublishState(KimiWebRuntimeState.Unavailable, null, reason);
        }
    }

    private void TrackCleanup(Task cleanup)
    {
        lock (_sync)
            _cleanupTask = Task.WhenAll(_cleanupTask, cleanup);
    }

    private async Task AwaitTrackedCleanupAsync()
    {
        Task cleanup;
        lock (_sync)
            cleanup = _cleanupTask;

        try { await cleanup.ConfigureAwait(false); }
        catch
        {
            // Lease disposal is best-effort and internally catches resource
            // failures. Keep this defensive guard so a cleanup regression
            // cannot permanently wedge the lifecycle gate.
        }

        lock (_sync)
        {
            if (ReferenceEquals(_cleanupTask, cleanup))
                _cleanupTask = Task.CompletedTask;
        }
    }

    internal Task CleanupTask
    {
        get { lock (_sync) return _cleanupTask; }
    }

    private void PublishState(
        KimiWebRuntimeState state,
        KimiWebErrorClass? error = null,
        KimiWebLaunchUnavailableReason? reason = null)
    {
        string? readyUrl = null;
        if (state == KimiWebRuntimeState.Ready)
        {
            lock (_sync)
                readyUrl = _readyUrl?.ToString();
        }

        _ = _bridge.SendEventAsync(new
        {
            type = "kimi_web_runtime_status",
            state = ToWireState(state),
            readyUrl,
            errorClass = error.HasValue ? ToWireErrorClass(error.Value) : null,
            reason = reason.HasValue ? ToWireReason(reason.Value) : null
        });
    }

    private static string ToWireErrorClass(KimiWebErrorClass error) => error switch
    {
        KimiWebErrorClass.LaunchFailed => "launch_failed",
        KimiWebErrorClass.StartTimeout => "start_timeout",
        KimiWebErrorClass.HealthCheckFailed => "health_check_failed",
        _ => "launch_failed"
    };

    private static string ToWireReason(KimiWebLaunchUnavailableReason reason) => reason switch
    {
        KimiWebLaunchUnavailableReason.PortableNodeMissing => "portable_node_missing",
        KimiWebLaunchUnavailableReason.RuntimeMissing => "runtime_missing",
        KimiWebLaunchUnavailableReason.RuntimeInvalid => "runtime_invalid",
        _ => "runtime_invalid"
    };

    private void Log(string message) =>
        RotatingDiagnosticLog.AppendLine(Path.Combine(_logDirectory, "kimi-web-supervisor.log"), message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lifetimeCts.Cancel();
        RequestStop();
        // Never block the caller (the UI thread) on the lifecycle lock or the
        // graceful shutdown wait. MainWindow awaits ShutdownAsync before
        // disposing; this keeps a non-blocking fallback for the path that
        // skips it.
        if (_lifecycleLock.Wait(0))
        {
            try { ImmediateStopHoldingLifecycleLock(); }
            finally { _lifecycleLock.Release(); }
        }
        else
        {
            ImmediateStopHoldingLifecycleLock();
        }

        _lifetimeCts.Dispose();
        _lifecycleLock.Dispose();
    }

    /// <summary>Non-graceful teardown used only by <see cref="Dispose"/> so
    /// the disposal path never blocks on the shutdown request + exit wait.</summary>
    private void ImmediateStopHoldingLifecycleLock()
    {
        KimiWebRuntimeLease? lease;
        lock (_publishLock)
        {
            lock (_sync)
            {
                _generation.Invalidate();
                lease = _lease;
                _lease = null;
                _readyUrl = null;
                _token = null;
                _state = KimiWebRuntimeState.Stopped;
            }
            FrameOriginChanged?.Invoke(null);
            PublishState(KimiWebRuntimeState.Stopped);
        }

        lease?.TearDown();
        lock (_sync)
            _stopRequested = false;
    }
}

/// <summary>
/// Monotonic start/stop epoch. Each launched process owns one token; Stop and
/// a newer Start invalidate every older token so stale monitors cannot publish.
/// </summary>
internal sealed class KimiWebRuntimeGenerationGate
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
/// could otherwise be part of a teardown chain. Only pipe-reader tasks are
/// tracked here; lifecycle monitors are guarded separately by the generation
/// gate. The disposal phase runs on the thread pool: it awaits the readers
/// (bounded), then disposes the streams, Process and job so repeated restarts
/// leak no handles without adding a self-wait delay to retry.
/// </summary>
internal sealed class KimiWebRuntimeLease
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

    public KimiWebRuntimeLease(long generation)
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

        // Disposal phase: a separate pool task awaits the tracked pipe readers,
        // then closes the streams which force any wedged reader loops to end.
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
