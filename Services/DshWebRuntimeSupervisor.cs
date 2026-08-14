using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

public enum DshRuntimeState { NotInstalled, Installing, Starting, Ready, Exited, Failed }

/// <summary>
/// Process-level singleton supervising the `dsh web` server: install, launch
/// with a Job Object (KILL_ON_JOB_CLOSE), parse the ready signal, health-check,
/// and broadcast dsh_runtime_status. Workspace ≠ process.
/// </summary>
public sealed class DshWebRuntimeSupervisor : IDisposable
{
    private static readonly Regex ReadyPattern =
        new(@"dsh\s+web:\s+(https?://[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DshWebRuntime _runtime;
    private readonly IAgentBridgeService _bridge;
    private readonly string _logDirectory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private DshRuntimeState _state = DshRuntimeState.NotInstalled;
    private Process? _process;
    private JobObjectHandle? _job;
    private Uri? _readyUrl;
    private bool _disposed;

    public DshWebRuntimeSupervisor(DshWebRuntime runtime, IAgentBridgeService bridge, string logDirectory)
    {
        _runtime = runtime;
        _bridge = bridge;
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
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

    /// <summary>Accept only the loopback HTTP origin DSH is allowed to bind.</summary>
    public static bool TryAcceptReadyUrl(string? candidate, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(parsed.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return false;
        url = parsed;
        return true;
    }

    /// <summary>Authority form used by FrameNavigationStarting (no trailing slash).</summary>
    public static string? ToFrameOrigin(Uri? url) =>
        url != null && TryAcceptReadyUrl(url.GetLeftPart(UriPartial.Authority), out var accepted)
            ? accepted.GetLeftPart(UriPartial.Authority)
            : null;

    public void PrepareForStartup() => _runtime.PrepareForStartup();

    public async Task InstallAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SetState(DshRuntimeState.Installing);
            var result = await _runtime.InstallAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(result.Success ? DshRuntimeState.NotInstalled : DshRuntimeState.Failed, result.ErrorMessage);
        }
        finally { _lifecycleLock.Release(); }
    }

    public async Task EnsureRunningAsync()
    {
        lock (_sync)
        {
            if (_state == DshRuntimeState.Ready && _readyUrl != null) return;
            if (_state == DshRuntimeState.Starting) return;
        }
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_state == DshRuntimeState.Ready && _readyUrl != null) return;
                if (_state == DshRuntimeState.Starting) return;
            }
            var spec = _runtime.CreateLaunchSpec();
            if (spec == null) { SetState(DshRuntimeState.NotInstalled); return; }
            await StartProcessAsync(spec).ConfigureAwait(false);
        }
        finally { _lifecycleLock.Release(); }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_process != null && !_process.HasExited)
                try { _process.Kill(entireProcessTree: true); } catch { }
            _process = null;
            _readyUrl = null;
        }
        _job?.Dispose();
        _job = null;
        ReadyUrlChanged?.Invoke(null);
        SetState(DshRuntimeState.Exited);
    }

    private async Task StartProcessAsync(DshLaunchSpec spec)
    {
        SetState(DshRuntimeState.Starting);
        var psi = new ProcessStartInfo
        {
            FileName = spec.NodePath,
            WorkingDirectory = _logDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(spec.EntryPath);
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("0");

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) { SetState(DshRuntimeState.Failed, "无法启动 DSH 进程：" + ex.Message); return; }
        if (process == null) { SetState(DshRuntimeState.Failed, "DSH 进程未启动。"); return; }

        lock (_sync) { _process = process; _readyUrl = null; }

        _job?.Dispose();
        _job = JobObjectHandle.TryCreateWithKillOnClose(Log);
        if (_job != null)
            try { _job.AssignProcess(process.Handle, Log); }
            catch (Exception ex) { Log($"WARN: job assign failed: {ex.Message}"); }

        _ = MonitorAsync(process);
    }

    private async Task MonitorAsync(Process process)
    {
        var readyTcs = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitedTcs.TrySetResult(true);

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var match = ReadyPattern.Match(line);
                    if (match.Success && TryAcceptReadyUrl(match.Groups[1].Value, out var uri))
                        readyTcs.TrySetResult(uri);
                }
            }
            catch { }
            finally { readyTcs.TrySetResult(null); }
        });

        _ = Task.Run(async () =>
        {
            try { string? line; while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) Log($"stderr: {line}"); }
            catch { }
        });

        var winner = await Task.WhenAny(readyTcs.Task, exitedTcs.Task, Task.Delay(TimeSpan.FromSeconds(90)))
            .ConfigureAwait(false);

        if (winner == readyTcs.Task && await readyTcs.Task.ConfigureAwait(false) is { } url)
        {
            if (await HealthCheckAsync(url).ConfigureAwait(false))
            {
                lock (_sync) { _readyUrl = url; }
                ReadyUrlChanged?.Invoke(url);
                SetState(DshRuntimeState.Ready);
                _ = WatchExitAsync(process, exitedTcs);
                return;
            }
        }
        if (!process.HasExited) try { process.Kill(entireProcessTree: true); } catch { }
        ClearReadyOrigin();
        SetState(DshRuntimeState.Failed,
            process.HasExited ? "DSH 进程意外退出。" : "DSH 启动超时（90 秒内未就绪）。");
    }

    private async Task WatchExitAsync(Process process, TaskCompletionSource<bool> exitedTcs)
    {
        try { await exitedTcs.Task.ConfigureAwait(false); }
        catch { return; }
        lock (_sync)
        {
            if (!ReferenceEquals(_process, process))
                return;
            _process = null;
            _readyUrl = null;
        }
        ClearReadyOrigin();
        SetState(DshRuntimeState.Exited);
    }

    private void ClearReadyOrigin() => ReadyUrlChanged?.Invoke(null);

    private static async Task<bool> HealthCheckAsync(Uri url)
    {
        if (!TryAcceptReadyUrl(url.GetLeftPart(UriPartial.Authority), out _))
            return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(url).ConfigureAwait(false);
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

    private void SetState(DshRuntimeState state, string? error = null)
    {
        Uri? readyUrl;
        lock (_sync)
        {
            _state = state;
            readyUrl = _readyUrl;
        }
        var wireError = state == DshRuntimeState.Failed ? (error ?? "运行时不可用") : (string?)null;
        _ = _bridge.SendEventAsync(new
        {
            type = "dsh_runtime_status",
            state = ToWireState(state),
            readyUrl = state == DshRuntimeState.Ready ? ToFrameOrigin(readyUrl) : null,
            errorClass = wireError
        });
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(_logDirectory, "dsh-supervisor.log"),
            $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _lifecycleLock.Dispose();
    }
}
