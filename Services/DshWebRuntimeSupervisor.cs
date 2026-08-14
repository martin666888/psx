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
    /// WebViewHostPolicy frame whitelist can be updated.</summary>
    public Action<Uri?>? ReadyUrlChanged { get; set; }

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
                    if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
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
                SetState(DshRuntimeState.Ready);
                ReadyUrlChanged?.Invoke(url);
                return;
            }
        }
        if (!process.HasExited) try { process.Kill(entireProcessTree: true); } catch { }
        SetState(DshRuntimeState.Failed,
            process.HasExited ? "DSH 进程意外退出。" : "DSH 启动超时（90 秒内未就绪）。");
    }

    private static async Task<bool> HealthCheckAsync(Uri url)
    {
        try { using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return (await c.GetAsync(url).ConfigureAwait(false)).IsSuccessStatusCode; }
        catch { return false; }
    }

    private void SetState(DshRuntimeState state, string? error = null)
    {
        lock (_sync) { _state = state; }
        var wireError = state == DshRuntimeState.Failed ? (error ?? "运行时不可用") : (string?)null;
        _ = _bridge.SendEventAsync(new
        {
            type = "dsh_runtime_status",
            state = state.ToString().ToLowerInvariant(),
            readyUrl = state == DshRuntimeState.Ready ? _readyUrl?.ToString() : null,
            errorClass = wireError
        });
    }

    private void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(_logDirectory, "dsh-supervisor.log"),
            $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}"); }
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
