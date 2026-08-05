using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// External Qoder CLI runtime. PSX discovers <c>qodercli</c> on PATH at startup
/// without executing it; version probing runs lazily when a Qoder Workspace needs
/// readiness. Updates and installation are owned by Qoder, not PSX.
/// </summary>
public sealed class QoderCliAcpRuntime : IAcpAgentRuntime
{
    public static readonly Version MinimumCompatibleVersion = new(0, 2, 11);

    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex VersionTokenPattern = new(@"\d+\.\d+\.\d+(?:[-+][\w.-]+)?", RegexOptions.Compiled);

    private readonly string _logPath;
    private readonly object _gate = new();

    private string? _discoveredPath;
    private string? _probedVersion;
    private bool _versionGatePassed;
    private bool _versionProbeAttempted;

    public QoderCliAcpRuntime(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "qoder-runtime.log");
    }

    public event Action<string>? StatusChanged;

    public string LogPath => _logPath;

    public bool SupportsSelfUpdate => false;

    public AcpRuntimeOwnershipKind OwnershipKind => AcpRuntimeOwnershipKind.External;

    /// <summary>
    /// Path-only entry for terminal profiles and login. May return a discovered
    /// shim before the version gate has passed (login must still be reachable).
    /// </summary>
    public string? TryGetDiscoveredEntryPath()
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_discoveredPath))
                return _discoveredPath;
        }

        var discovered = DiscoverEntryPath();
        lock (_gate)
        {
            _discoveredPath ??= discovered;
            return _discoveredPath;
        }
    }

    public bool HasDiscoveredEntry
    {
        get
        {
            lock (_gate)
                return !string.IsNullOrWhiteSpace(_discoveredPath);
        }
    }

    public RuntimeExternalUiHints GetExternalUiHints() => new(
        InstallDocsUrl: "https://docs.qoder.com/cli/install",
        InstallCommandHint: "npm install -g @qoder-ai/qodercli",
        OwnershipLabel: "外部安装，由 Qoder 管理");

    RuntimeExternalUiHints? IAcpAgentRuntime.GetExternalUiHints() => GetExternalUiHints();

    public bool IsReady()
    {
        lock (_gate)
            return _versionGatePassed;
    }

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        lock (_gate)
        {
            return new RuntimeVersionSnapshot(
                CurrentVersion: _probedVersion,
                PendingVersion: null,
                HasPendingUpdate: false)
            {
                ProductName = "Qoder CLI"
            };
        }
    }

    public string BuildStatusText(string? suffix = null)
    {
        lock (_gate)
        {
            var version = _probedVersion;
            var baseText = string.IsNullOrWhiteSpace(version)
                ? "Qoder CLI（外部安装，由 Qoder 管理）"
                : $"Qoder CLI {version}（外部安装，由 Qoder 管理）";
            return string.IsNullOrWhiteSpace(suffix) ? baseText : $"{baseText} · {suffix}";
        }
    }

    public Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var discovered = DiscoverEntryPath();
        lock (_gate)
        {
            _discoveredPath = discovered;
            if (discovered == null)
            {
                _probedVersion = null;
                _versionGatePassed = false;
                _versionProbeAttempted = false;
            }
        }

        if (discovered != null)
            Log($"Discovered qodercli at {discovered} (path-only, no execution).");
        else
            Log("No qodercli entry found on PATH during startup discovery.");

        return Task.CompletedTask;
    }

    public async Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var probeResult = await ProbeVersionAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(BuildStatusText());
        return probeResult;
    }

    public Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string message = "Qoder 由外部管理，请使用 Qoder 自身更新";
        StatusChanged?.Invoke(message);
        return Task.FromResult(new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed, message));
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        string? entryPath;
        lock (_gate)
        {
            if (!_versionGatePassed || string.IsNullOrWhiteSpace(_discoveredPath))
            {
                throw new InvalidOperationException(
                    "Qoder CLI runtime is not ready. Install or upgrade qodercli before starting a session.");
            }

            entryPath = _discoveredPath;
        }

        var environment = new Dictionary<string, string?>();
        ForwardEnvironmentVariable(environment, "PATH");
        ForwardEnvironmentVariable(environment, "QODER_PERSONAL_ACCESS_TOKEN");

        return new AcpProcessSpec
        {
            FileName = entryPath,
            WorkingDirectory = workingDirectory,
            Arguments = new[] { "--acp" },
            Environment = environment
        };
    }

    public void Dispose()
    {
    }

    internal async Task<AcpRuntimeOperationResult> ProbeVersionAsync(CancellationToken cancellationToken)
    {
        string? entryPath;
        lock (_gate)
        {
            entryPath = _discoveredPath ?? DiscoverEntryPath();
            _discoveredPath = entryPath;
            _versionProbeAttempted = true;
        }

        if (string.IsNullOrWhiteSpace(entryPath))
        {
            const string message =
                "未在 PATH 中找到 qodercli。请安装 Qoder CLI：npm install -g @qoder-ai/qodercli，详见 https://docs.qoder.com/cli/install";
            Log(message);
            lock (_gate)
            {
                _probedVersion = null;
                _versionGatePassed = false;
            }

            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, message);
        }

        var probe = await RunVersionProbeAsync(entryPath, cancellationToken).ConfigureAwait(false);
        if (probe.Error != null)
        {
            var message =
                $"无法读取 qodercli 版本：{probe.Error}。请确认已安装 Qoder CLI 并可通过 qodercli --version 运行。";
            Log(message);
            lock (_gate)
            {
                _probedVersion = null;
                _versionGatePassed = false;
            }

            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, message, probe.ExitCode);
        }

        if (!TryParseVersion(probe.Output, out var parsedVersion, out var versionText))
        {
            const string message =
                "无法解析 qodercli --version 输出。请升级 Qoder CLI 后重试。";
            Log($"{message} Raw output: {probe.Output}");
            lock (_gate)
            {
                _probedVersion = null;
                _versionGatePassed = false;
            }

            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, message, probe.ExitCode);
        }

        lock (_gate)
            _probedVersion = versionText;

        if (parsedVersion < MinimumCompatibleVersion)
        {
            var message =
                $"当前 Qoder CLI 版本 {versionText} 低于 PSX 所需的最低版本 {MinimumCompatibleVersion}。" +
                "请通过 Qoder 自身更新 qodercli 后重试。";
            Log(message);
            lock (_gate)
                _versionGatePassed = false;

            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, message, probe.ExitCode);
        }

        Log($"Version gate passed: {versionText} >= {MinimumCompatibleVersion}.");
        lock (_gate)
            _versionGatePassed = true;

        return new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.AlreadyReady,
            BuildStatusText());
    }

    internal bool VersionProbeAttempted
    {
        get
        {
            lock (_gate)
                return _versionProbeAttempted;
        }
    }

    internal static bool SkipNpmShimDiscovery { get; set; }

    internal static string? DiscoverEntryPath()
    {
        foreach (var directory in EnumeratePathDirectories())
        {
            var cmdPath = Path.Combine(directory, "qodercli.cmd");
            if (File.Exists(cmdPath))
                return cmdPath;

            var exePath = Path.Combine(directory, "qodercli.exe");
            if (File.Exists(exePath))
                return exePath;

            var barePath = Path.Combine(directory, "qodercli");
            if (File.Exists(barePath))
                return barePath;
        }

        if (SkipNpmShimDiscovery)
            return null;

        var npmShim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "qodercli.cmd");
        return File.Exists(npmShim) ? npmShim : null;
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            yield break;

        foreach (var segment in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(segment))
                yield return segment;
        }
    }

    private static bool TryParseVersion(string output, out Version parsed, out string versionText)
    {
        parsed = new Version(0, 0);
        versionText = "";
        var match = VersionTokenPattern.Match(output);
        if (!match.Success)
            return false;

        versionText = match.Value ?? "";
        if (!Version.TryParse(NormalizeVersionText(versionText), out var parsedVersion))
            return false;

        parsed = parsedVersion;
        return true;
    }

    private static string NormalizeVersionText(string versionText)
    {
        var plusIndex = versionText.IndexOf('+');
        if (plusIndex >= 0)
            versionText = versionText[..plusIndex];

        var dashIndex = versionText.IndexOf('-');
        if (dashIndex >= 0)
            versionText = versionText[..dashIndex];

        return versionText;
    }

    private async Task<(string Output, string? Error, int? ExitCode)> RunVersionProbeAsync(
        string entryPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = entryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--version");

        Log($"Probing version: {entryPath} --version");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, null);
        }

        if (process == null)
            return ("", "process did not start", null);

        using var processLifetime = process;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(VersionProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return ("", $"--version timed out after {VersionProbeTimeout.TotalSeconds:0} seconds", null);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (!string.IsNullOrWhiteSpace(stdout))
            Log($"stdout:\n{stdout}");
        if (!string.IsNullOrWhiteSpace(stderr))
            Log($"stderr:\n{stderr}");
        Log($"--version exited with code {process.ExitCode}.");

        if (process.ExitCode != 0)
            return (stdout, string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}" : stderr, process.ExitCode);

        return (string.IsNullOrWhiteSpace(stdout) ? stderr : stdout, null, process.ExitCode);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void ForwardEnvironmentVariable(IDictionary<string, string?> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            environment[name] = value;
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Logs are best-effort.
        }
    }
}
