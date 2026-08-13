using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// User-confirmed Cline installation plus Qoder-style staged self-update. The
/// release ships manifests only; first install uses npm ci into a scratch
/// directory and a rollback-safe same-volume swap onto
/// <c>runtime/cline-current</c>. Toolbar updates query npm latest, install the
/// matching wrapper and Windows x64 platform packages into
/// <c>runtime/cline-next</c>, flip <c>cline-active.txt</c>, and promote on the
/// next PSX launch — never mid-session. Cline's own updater stays disabled.
/// </summary>
public sealed class ClineAcpRuntime : IAcpAgentRuntime
{
    public const string WrapperPackageName = "cline";
    public const string PlatformPackageName = "@cline/cli-windows-x64";
    public const string SeededPackageVersion = "3.0.53";
    public static readonly Version MinimumCompatibleVersion = new(3, 0, 53);

    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";

    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SmokeCheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly string WrapperPackageDirectory =
        Path.Combine("node_modules", "cline");
    private static readonly string WrapperPackageJson =
        Path.Combine(WrapperPackageDirectory, "package.json");
    private static readonly string PlatformPackageDirectory =
        Path.Combine("node_modules", "@cline", "cli-windows-x64");
    private static readonly string PlatformPackageJson =
        Path.Combine(PlatformPackageDirectory, "package.json");
    private static readonly string PlatformExecutable =
        Path.Combine(PlatformPackageDirectory, "bin", "cline.exe");

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;
    private readonly NpmRuntimeProcessRunner _npmRunner;
    private readonly StagedRuntimeStore _stagedStore;
    private readonly ClineLaunchPolicy _launchPolicy = new();
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public ClineAcpRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, DefaultProcessTimeout)
    {
    }

    internal ClineAcpRuntime(
        RuntimeLocator locator,
        string logDirectory,
        TimeSpan processTimeout)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        if (processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "cline-runtime.log");
        _npmRunner = new NpmRuntimeProcessRunner(processTimeout, Log);
        _stagedStore = new StagedRuntimeStore("Cline", Log);
    }

    public event Action<string>? StatusChanged;

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    public bool SupportsSelfUpdate => true;

    public bool IsReady()
        => ValidateRoot(Paths, Paths.ClineCurrentDirectory, ClineVersionPolicy.MinimumCompatible, out _) == null;

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        var paths = Paths;
        var currentVersion = ReadPackageVersion(Path.Combine(paths.ClineCurrentDirectory, WrapperPackageJson));
        string? pendingVersion = null;
        if (PointerSaysNext(paths)
            && ValidateRoot(paths, paths.ClineNextDirectory, ClineVersionPolicy.MinimumCompatible, out _) == null)
        {
            pendingVersion = ReadPackageVersion(Path.Combine(paths.ClineNextDirectory, WrapperPackageJson));
        }

        var hasPending = pendingVersion != null
            && !string.Equals(pendingVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        return new RuntimeVersionSnapshot(
            CurrentVersion: currentVersion,
            PendingVersion: hasPending ? pendingVersion : null,
            HasPendingUpdate: hasPending)
        {
            ProductName = "Cline"
        };
    }

    public string BuildStatusText(string? suffix = null)
    {
        var snapshot = GetVersionSnapshot();
        var text = IsReady()
            ? $"Cline {snapshot.CurrentVersion}"
            : "Cline 未安装 · 首次使用时安装";
        return string.IsNullOrWhiteSpace(suffix) ? text : $"{text} · {suffix}";
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;
            RecoverInterruptedSwap(paths);
            if (PointerSaysNext(paths))
            {
                if (ValidateRoot(paths, paths.ClineNextDirectory, ClineVersionPolicy.MinimumCompatible, out _) != null)
                {
                    Log("Cline promote aborted: cline-next is incomplete; reverting pointer to 'current'.");
                    TryWriteActivePointer(paths, ActiveCurrentToken);
                }
                else
                {
                    PromoteNextToCurrent(paths);
                }
            }
            else if (File.Exists(paths.ClineActivePointerFile))
            {
                TryWriteActivePointer(paths, ActiveCurrentToken);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsReady())
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Cline runtime is already installed.");
        }

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady())
            {
                StatusChanged?.Invoke(BuildStatusText());
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.AlreadyReady,
                    "Cline runtime is already installed.");
            }

            var paths = Paths;
            RecoverInterruptedSwap(paths);
            if (IsReady())
            {
                StatusChanged?.Invoke(BuildStatusText());
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.AlreadyReady,
                    "Cline runtime is already installed.");
            }

            var preflight = CheckInstallInputs(paths);
            if (preflight != null)
                return preflight;

            StatusChanged?.Invoke("正在安装 Cline");
            try
            {
                Directory.CreateDirectory(paths.RuntimeRoot);
                DeleteDirectory(paths.ClineInstallingDirectory);
                Directory.CreateDirectory(paths.ClineInstallingDirectory);
                CopySeedFiles(paths, paths.ClineInstallingDirectory, includeLockfile: true);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare Cline staging directory: {ex}");
                StatusChanged?.Invoke("Cline 安装失败，Agent 暂不可用");
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "Failed to prepare the Cline installation directory.");
            }

            var install = await _npmRunner.RunAsync(
                paths.PortableNodePath!,
                paths.PortableNpmCliPath!,
                paths.ClineInstallingDirectory,
                "npm ci for pinned Cline runtime",
                ["ci", "--omit=dev", "--include=optional", "--ignore-scripts", "--no-audit", "--no-fund"],
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                TryDeleteStaging(paths);
                StatusChanged?.Invoke(install.Kind == AcpRuntimeOperationKind.Cancelled
                    ? "Cline 安装已取消"
                    : "Cline 安装失败，原运行时保持不变");
                return new AcpRuntimeOperationResult(install.Kind, install.Message, install.ExitCode);
            }

            var validationError = ValidateRoot(
                paths,
                paths.ClineInstallingDirectory,
                ClineVersionPolicy.PinnedSeed,
                out var staged);
            if (validationError != null || staged == null)
            {
                Log($"Cline staged validation failed: {validationError}");
                TryDeleteStaging(paths);
                StatusChanged?.Invoke("Cline 安装失败，原运行时保持不变");
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "Installed Cline files failed validation; the previous runtime was not changed.");
            }

            var smokeError = await RunSmokeCheckAsync(
                paths,
                paths.ClineInstallingDirectory,
                staged,
                SeededPackageVersion,
                cancellationToken).ConfigureAwait(false);
            if (smokeError != null)
            {
                Log($"Cline staged smoke failed: {smokeError}");
                TryDeleteStaging(paths);
                if (cancellationToken.IsCancellationRequested)
                {
                    StatusChanged?.Invoke("Cline 安装已取消");
                    return new AcpRuntimeOperationResult(
                        AcpRuntimeOperationKind.Cancelled,
                        "Cline installation was cancelled during its start check.");
                }
                StatusChanged?.Invoke("Cline 安装失败，原运行时保持不变");
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "Installed Cline failed its start check; the previous runtime was not changed.");
            }

            if (!ActivateStagedRuntime(paths))
            {
                StatusChanged?.Invoke("Cline 安装失败，已尝试恢复原运行时");
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "Cline was installed but could not be activated safely.");
            }

            ClearStaleNext(paths);
            TryWriteActivePointer(paths, ActiveCurrentToken);
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Success,
                $"Cline {SeededPackageVersion} installed successfully.");
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Paths;
        if (ValidateRoot(paths, paths.ClineCurrentDirectory, ClineVersionPolicy.MinimumCompatible, out _) != null)
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Cline runtime is not installed; skipping refresh.");
        }

        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                "Portable npm is not available; cannot refresh Cline.");
        }

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke(BuildStatusText("正在检查更新"));

            Directory.CreateDirectory(paths.RuntimeRoot);
            var view = await _npmRunner.RunAsync(
                paths.PortableNodePath!,
                paths.PortableNpmCliPath!,
                paths.RuntimeRoot,
                $"npm view {WrapperPackageName}@latest version",
                ["view", $"{WrapperPackageName}@latest", "version", "--json"],
                cancellationToken).ConfigureAwait(false);
            if (view.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(view.Kind, view.Message, view.ExitCode);
            }

            var candidate = ParseNpmViewVersion(view.Stdout);
            var currentVersion = ReadPackageVersion(Path.Combine(paths.ClineCurrentDirectory, WrapperPackageJson));
            if (!string.IsNullOrWhiteSpace(candidate)
                && !string.IsNullOrWhiteSpace(currentVersion)
                && string.Equals(candidate, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                ClearStaleNext(paths);
                TryWriteActivePointer(paths, ActiveCurrentToken);
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.AlreadyReady,
                    $"Cline {currentVersion} 已是最新版本。");
            }

            if (!Version.TryParse(candidate, out var parsedCandidate)
                || !Version.TryParse(currentVersion, out var parsedCurrent))
            {
                Log($"Cline version comparison is unsafe: registry='{candidate}', current='{currentVersion}'. Not updating.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "无法安全比较 Registry 版本，未执行更新。");
            }

            if (parsedCandidate < parsedCurrent)
            {
                Log($"Registry Cline {candidate} is older than current {currentVersion}; refusing to downgrade.");
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.AlreadyReady,
                    $"Registry 版本（{candidate}）低于当前版本（{currentVersion}），未执行更新。");
            }

            if (parsedCandidate < MinimumCompatibleVersion)
            {
                Log($"Registry Cline {candidate} is below MinimumCompatibleVersion {MinimumCompatibleVersion}.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Registry 版本（{candidate}）低于 PSX 所需的最低版本 {MinimumCompatibleVersion}，未执行更新。");
            }

            try
            {
                if (Directory.Exists(paths.ClineNextDirectory))
                    Directory.Delete(paths.ClineNextDirectory, recursive: true);
                Directory.CreateDirectory(paths.ClineNextDirectory);
                CopySeedFiles(paths, paths.ClineNextDirectory, includeLockfile: false);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare cline-next: {ex}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Failed to prepare cline-next: {ex.Message}");
            }

            var installLabel = $"npm install {WrapperPackageName}@{candidate} {PlatformPackageName}@{candidate}";
            StatusChanged?.Invoke(BuildStatusText("正在更新 Cline"));
            var install = await _npmRunner.RunAsync(
                paths.PortableNodePath!,
                paths.PortableNpmCliPath!,
                paths.ClineNextDirectory,
                installLabel,
                [
                    "install",
                    $"{WrapperPackageName}@{candidate}",
                    $"{PlatformPackageName}@{candidate}",
                    "--save-exact",
                    "--omit=dev",
                    "--include=optional",
                    "--ignore-scripts",
                    "--no-audit",
                    "--no-fund"
                ],
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(install.Kind, install.Message, install.ExitCode);
            }

            var stagedError = ValidateRoot(
                paths,
                paths.ClineNextDirectory,
                ClineVersionPolicy.MinimumCompatible,
                out var staged);
            if (stagedError != null || staged == null)
            {
                Log($"Post-update validation failed: {stagedError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Updated Cline is incomplete; not switching to it. ({stagedError})");
            }

            if (!string.Equals(staged.Version, candidate, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Staged Cline version '{staged.Version}' does not match requested '{candidate}'.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Staged Cline version ({staged.Version}) does not match the requested {candidate}; not switching to it.");
            }

            var smokeError = await RunSmokeCheckAsync(
                paths,
                paths.ClineNextDirectory,
                staged,
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (smokeError != null)
            {
                Log($"Staged Cline smoke check failed: {smokeError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Staged Cline failed its start check; not switching to it. ({smokeError})");
            }

            if (!TryWriteActivePointer(paths, ActiveNextToken))
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "已下载更新，但无法写入激活指针，未切换版本。");
            }

            Log($"cline-next staged at {candidate} and verified; active pointer flipped to 'next'.");
            StatusChanged?.Invoke(BuildStatusText($"已更新到 Cline {candidate}，下次启动生效"));
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Success,
                $"{installLabel} completed successfully.");
        }
        finally
        {
            _installLock.Release();
        }
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        var paths = Paths;
        var error = ValidateRoot(
            paths,
            paths.ClineCurrentDirectory,
            ClineVersionPolicy.MinimumCompatible,
            out var entry);
        if (error != null || entry == null)
            throw new InvalidOperationException("Cline runtime is not installed. Confirm installation from Agent mode first.");

        return _launchPolicy.CreateAcpProcessSpec(
            paths.PortableNodePath!,
            entry.WrapperPath,
            entry.ExecutablePath,
            workingDirectory);
    }

    public string? TryBuildInteractivePowerShellInvocation(params string[] arguments)
    {
        var paths = Paths;
        var error = ValidateRoot(
            paths,
            paths.ClineCurrentDirectory,
            ClineVersionPolicy.MinimumCompatible,
            out var entry);
        if (error != null || entry == null)
            return null;

        return _launchPolicy.CreatePowerShellInvocation(
            paths.PortableNodePath!,
            entry.WrapperPath,
            entry.ExecutablePath,
            arguments);
    }

    public void Dispose() => _installLock.Dispose();

    private AcpRuntimeOperationResult? CheckInstallInputs(RuntimePaths paths)
    {
        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, "Portable npm is unavailable.");

        foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
        {
            if (!File.Exists(Path.Combine(paths.ClineSeedDirectory, name)))
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, "Cline seed files are missing.");
        }

        try
        {
            Directory.CreateDirectory(paths.RuntimeRoot);
            var probe = Path.Combine(paths.RuntimeRoot, $".cline-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            Log($"Cline runtime root is not writable: {ex}");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                "PSX cannot write to its runtime folder. Extract PSX to a writable folder and retry.");
        }

        return null;
    }

    private static void CopySeedFiles(RuntimePaths paths, string destination, bool includeLockfile)
    {
        var names = includeLockfile
            ? new[] { "package.json", "package-lock.json", ".npmrc" }
            : new[] { "package.json", ".npmrc" };
        foreach (var name in names)
        {
            var source = Path.Combine(paths.ClineSeedDirectory, name);
            if (!File.Exists(source))
                continue;
            File.Copy(source, Path.Combine(destination, name), overwrite: true);
        }
    }

    private bool ActivateStagedRuntime(RuntimePaths paths)
    {
        var currentMovedToRollback = false;
        var stagedMovedToCurrent = false;
        try
        {
            DeleteDirectory(paths.ClineRollbackDirectory);
            if (Directory.Exists(paths.ClineCurrentDirectory))
            {
                Directory.Move(paths.ClineCurrentDirectory, paths.ClineRollbackDirectory);
                currentMovedToRollback = true;
            }
            Directory.Move(paths.ClineInstallingDirectory, paths.ClineCurrentDirectory);
            stagedMovedToCurrent = true;

            var error = ValidateRoot(
                paths,
                paths.ClineCurrentDirectory,
                ClineVersionPolicy.PinnedSeed,
                out _);
            if (error != null)
                throw new InvalidOperationException(error);
        }
        catch (Exception ex)
        {
            Log($"Cline activation failed; recovering: {ex}");
            try
            {
                if (stagedMovedToCurrent && Directory.Exists(paths.ClineCurrentDirectory))
                    DeleteDirectory(paths.ClineCurrentDirectory);
                if (currentMovedToRollback
                    && Directory.Exists(paths.ClineRollbackDirectory)
                    && !Directory.Exists(paths.ClineCurrentDirectory))
                {
                    Directory.Move(paths.ClineRollbackDirectory, paths.ClineCurrentDirectory);
                }
            }
            catch (Exception rollbackError)
            {
                Log($"Cline rollback failed: {rollbackError}");
            }
            return false;
        }

        // Activation is already committed. Failure to remove the backup must
        // not roll a valid new current back to an old release.
        try { DeleteDirectory(paths.ClineRollbackDirectory); }
        catch (Exception ex) { Log($"WARN: Cline rollback cleanup was deferred: {ex}"); }
        Log("Cline activation completed; staging is now current.");
        return true;
    }

    private void RecoverInterruptedSwap(RuntimePaths paths)
    {
        try
        {
            var currentValid = ValidateRoot(
                paths,
                paths.ClineCurrentDirectory,
                ClineVersionPolicy.MinimumCompatible,
                out _) == null;
            if (currentValid)
            {
                DeleteDirectory(paths.ClineInstallingDirectory);
                DeleteDirectory(paths.ClineRollbackDirectory);
                return;
            }

            if (!Directory.Exists(paths.ClineCurrentDirectory)
                && Directory.Exists(paths.ClineRollbackDirectory))
            {
                Directory.Move(paths.ClineRollbackDirectory, paths.ClineCurrentDirectory);
                Log("Recovered the previous Cline runtime after an interrupted activation.");
            }
            else if (!currentValid && Directory.Exists(paths.ClineRollbackDirectory))
            {
                DeleteDirectory(paths.ClineCurrentDirectory);
                Directory.Move(paths.ClineRollbackDirectory, paths.ClineCurrentDirectory);
                Log("Replaced an incomplete Cline current directory with its rollback copy.");
            }

            DeleteDirectory(paths.ClineInstallingDirectory);
        }
        catch (Exception ex)
        {
            Log($"WARN: Cline startup recovery could not finish: {ex}");
        }
    }

    private async Task<string?> RunSmokeCheckAsync(
        RuntimePaths paths,
        string workingDirectory,
        ClineEntry entry,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.PortableNodePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(entry.WrapperPath);
        startInfo.ArgumentList.Add("--version");
        foreach (var variable in _launchPolicy.CreateEnvironment(entry.ExecutablePath))
        {
            if (variable.Value == null)
                startInfo.Environment.Remove(variable.Key);
            else
                startInfo.Environment[variable.Key] = variable.Value;
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return $"failed to start the Cline wrapper: {ex.Message}";
        }
        if (process == null)
            return "the Cline wrapper process did not start";
        using var processLifetime = process;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SmokeCheckTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdoutTask, stderrTask, Log)
                .ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? "the Cline start check was cancelled"
                : "the Cline start check timed out";
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            Log($"Cline smoke stderr: {stderr}");
            return $"--version exited with code {process.ExitCode}";
        }
        if (!string.Equals(stdout, expectedVersion, StringComparison.OrdinalIgnoreCase))
            return $"--version returned '{stdout}' instead of '{expectedVersion}'";
        return null;
    }

    private static string? ValidateRoot(
        RuntimePaths paths,
        string root,
        ClineVersionPolicy versionPolicy,
        out ClineEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(paths.PortableNodePath) || !File.Exists(paths.PortableNodePath))
            return "portable Node.js is missing";
        if (!Directory.Exists(root))
            return "Cline runtime directory is missing";

        var wrapperManifest = Path.Combine(root, WrapperPackageJson);
        var platformManifest = Path.Combine(root, PlatformPackageJson);
        var wrapperVersion = ReadPackageVersion(wrapperManifest);
        var platformVersion = ReadPackageVersion(platformManifest);
        if (string.IsNullOrWhiteSpace(wrapperVersion) || string.IsNullOrWhiteSpace(platformVersion))
            return "Cline package manifests are missing or invalid";
        if (!string.Equals(wrapperVersion, platformVersion, StringComparison.OrdinalIgnoreCase))
            return "Cline wrapper and platform package versions do not match";
        if (versionPolicy == ClineVersionPolicy.PinnedSeed
            && !string.Equals(wrapperVersion, SeededPackageVersion, StringComparison.OrdinalIgnoreCase))
            return $"Cline version {wrapperVersion} does not match pinned {SeededPackageVersion}";
        if (versionPolicy == ClineVersionPolicy.MinimumCompatible)
        {
            if (!Version.TryParse(wrapperVersion, out var parsed)
                || parsed < MinimumCompatibleVersion)
            {
                return $"Cline version {wrapperVersion} is below the required {MinimumCompatibleVersion}";
            }
        }

        var wrapperPackageRoot = Path.GetFullPath(Path.Combine(root, WrapperPackageDirectory));
        var wrapperPath = ResolveBinPath(wrapperManifest, wrapperPackageRoot, "cline");
        if (wrapperPath == null || !File.Exists(wrapperPath))
            return "Cline wrapper entry is missing";

        var executablePath = Path.GetFullPath(Path.Combine(root, PlatformExecutable));
        if (!IsWithin(Path.GetFullPath(Path.Combine(root, PlatformPackageDirectory)), executablePath)
            || !File.Exists(executablePath))
            return "Cline Windows x64 executable is missing";

        entry = new ClineEntry(wrapperPath, executablePath, wrapperVersion);
        return null;
    }

    private static string? ResolveBinPath(string manifestPath, string packageRoot, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("bin", out var bin))
                return null;
            string? relative = bin.ValueKind == JsonValueKind.String
                ? bin.GetString()
                : bin.ValueKind == JsonValueKind.Object
                  && bin.TryGetProperty(key, out var keyed)
                  && keyed.ValueKind == JsonValueKind.String
                    ? keyed.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(relative))
                return null;
            var resolved = Path.GetFullPath(Path.Combine(packageRoot, relative));
            return IsWithin(packageRoot, resolved) ? resolved : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadPackageVersion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseNpmViewVersion(string stdout)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : null;
        }
        catch
        {
            return trimmed.Contains('"') || trimmed.Contains('{') ? null : trimmed;
        }
    }

    private void PromoteNextToCurrent(RuntimePaths paths)
    {
        if (_stagedStore.PromoteNextToCurrent(
                paths.RuntimeRoot,
                paths.ClineCurrentDirectory,
                paths.ClineNextDirectory,
                paths.ClineActivePointerFile,
                ActiveCurrentToken))
        {
            StatusChanged?.Invoke(BuildStatusText());
        }
    }

    private void ClearStaleNext(RuntimePaths paths)
        => _stagedStore.ClearStaleNext(paths.ClineNextDirectory);

    private bool PointerSaysNext(RuntimePaths paths)
        => _stagedStore.PointerSaysNext(paths.ClineActivePointerFile, ActiveNextToken);

    private bool TryWriteActivePointer(RuntimePaths paths, string token)
        => _stagedStore.TryWriteActivePointer(paths.RuntimeRoot, paths.ClineActivePointerFile, token);

    private void TryDeleteStaging(RuntimePaths paths)
    {
        try { DeleteDirectory(paths.ClineInstallingDirectory); }
        catch (Exception ex) { Log($"WARN: could not remove Cline staging: {ex}"); }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Runtime logging is diagnostic only.
        }
    }

    private enum ClineVersionPolicy
    {
        PinnedSeed,
        MinimumCompatible
    }

    private sealed record ClineEntry(string WrapperPath, string ExecutablePath, string Version);
}
