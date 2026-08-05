using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Runtime for the Qwen Code CLI. The baseline install ships inside the PSX
/// release at <c>{InstallDir}/tools/qwen/</c> (pre-installed at build time).
/// On top of that baseline the runtime supports the same two-directory
/// self-update model as <see cref="AcpRuntimeManager"/>: background
/// <c>npm install @qwen-code/qwen-code@latest</c> stages into the writable
/// <c>runtime/qwen-next/</c>, a pointer file flips to "next", and the next PSX
/// launch promotes it to <c>runtime/qwen-current/</c>. A structurally valid
/// <c>qwen-current</c> takes precedence over the bundled copy; deleting it (or
/// shipping a newer bundled version with a PSX release) falls back to the
/// bundled baseline.
///
/// Qwen speaks ACP natively via <c>node &lt;entry&gt; --acp</c> over stdio. The
/// entry point is resolved from the package's <c>bin.qwen</c> field, with a
/// guard preventing the bin path from escaping the package directory.
/// </summary>
public sealed class QwenCodeAcpRuntime : IAcpAgentRuntime
{
    private const string QwenPackageName = "@qwen-code/qwen-code";
    private const string PackageLockName = "package-lock.json";
    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";
    private const string QwenSystemSettingsFileName = "qwen-system-settings.json";
    private const string QwenSystemSettingsContent = "{\"general\":{\"enableAutoUpdate\":false}}";

    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Staged-entry <c>--version</c> smoke check budget.</summary>
    private static readonly TimeSpan SmokeCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string QwenPackageDirSubpath =
        Path.Combine("node_modules", "@qwen-code", "qwen-code");
    private static readonly string QwenPackageJsonSubpath =
        Path.Combine(QwenPackageDirSubpath, "package.json");

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;
    private readonly TimeSpan _processTimeout;

    /// <summary>
    /// Serializes npm staging and the startup promote so they never rewrite
    /// the same directories concurrently.
    /// </summary>
    private readonly SemaphoreSlim _npmLock = new(1, 1);

    /// <summary>
    /// Fired with human-readable status messages. Subscribers (the UI) are
    /// responsible for marshalling to the UI thread.
    /// </summary>
    public event Action<string>? StatusChanged;

    public QwenCodeAcpRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, DefaultProcessTimeout)
    {
    }

    internal QwenCodeAcpRuntime(RuntimeLocator locator, string logDirectory, TimeSpan processTimeout)
    {
        if (processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processTimeout = processTimeout;
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "qwen-runtime.log");
    }

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    /// <summary>Qwen Code self-updates through the qwen-current/qwen-next staging model.</summary>
    public bool SupportsSelfUpdate => true;

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        var paths = Paths;
        var currentVersion = ReadQwenVersion(ResolveActiveQwenRoot(paths));
        string? pendingVersion = null;
        if (PointerSaysNext(paths)
            && ValidateQwenRoot(paths, paths.QwenNextDirectory, requireLockfile: false, out _) == null)
        {
            pendingVersion = ReadQwenVersion(paths.QwenNextDirectory);
        }

        var hasPending = pendingVersion != null
            && !string.Equals(pendingVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        return new RuntimeVersionSnapshot(
            CurrentVersion: currentVersion,
            PendingVersion: hasPending ? pendingVersion : null,
            HasPendingUpdate: hasPending)
        {
            ProductName = "Qwen Code"
        };
    }

    /// <summary>
    /// True only when the active install is structurally valid: portable Node,
    /// the Qwen package manifest, a resolvable <c>bin.qwen</c> entry that stays
    /// inside the package directory, and the resolved entry file itself (the
    /// bundled baseline additionally requires its pinned lockfile).
    /// </summary>
    public bool IsReady()
    {
        var paths = Paths;
        return ValidateActiveInstall(paths, out _) == null;
    }

    /// <summary>
    /// Startup promote, mirroring <see cref="AcpRuntimeManager.TryPromoteNextToCurrentAsync"/>:
    /// when the pointer says "next" and the staged directory is valid, swap it
    /// into <c>qwen-current</c>. Afterwards, if a PSX release shipped a bundled
    /// Qwen that is the same or newer than the self-updated copy, drop the
    /// runtime copies and fall back to the bundled baseline.
    /// </summary>
    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;
            if (PointerSaysNext(paths))
            {
                if (ValidateQwenRoot(paths, paths.QwenNextDirectory, requireLockfile: false, out _) != null)
                {
                    Log("Qwen promote aborted: qwen-next is incomplete; reverting pointer to 'current'.");
                    TryWriteActivePointer(paths, ActiveCurrentToken);
                }
                else
                {
                    PromoteNextToCurrent(paths);
                }
            }
            else if (File.Exists(paths.QwenActivePointerFile))
            {
                TryWriteActivePointer(paths, ActiveCurrentToken);
            }

            DropRuntimeCopyWhenBundledIsSameOrNewer(paths);
        }
        finally
        {
            _npmLock.Release();
        }
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        var paths = Paths;
        var validationError = ValidateActiveInstall(paths, out var entryPath);
        if (validationError != null || entryPath == null)
        {
            throw new InvalidOperationException(
                validationError
                ?? $"Qwen Code runtime is not installed correctly under {paths.BundledQwenDirectory}. " +
                   "Re-extract or re-download PSX.");
        }

        var nodePath = paths.PortableNodePath;
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new InvalidOperationException(
                "Portable Node.js was not found next to PSX.exe. The release zip should include tools/node/node.exe.");
        }

        var systemSettingsPath = EnsureQwenSystemSettingsFile(paths);

        // Disable Qwen auto-update without setting QWEN_HOME. Forward PATH and
        // optional API credentials only when present in this process.
        var environment = new Dictionary<string, string?>();
        environment["QWEN_CODE_SYSTEM_SETTINGS_PATH"] = systemSettingsPath;
        ForwardEnvironmentVariable(environment, "PATH");
        ForwardEnvironmentVariable(environment, "OPENAI_API_KEY");

        return new AcpProcessSpec
        {
            FileName = nodePath,
            WorkingDirectory = workingDirectory,
            Arguments = new[] { entryPath, "--acp" },
            Environment = environment
        };
    }

    public string BuildStatusText(string? suffix = null)
    {
        var paths = Paths;
        var activeRoot = ResolveActiveQwenRoot(paths);
        var version = ReadQwenVersion(activeRoot);
        var isBundled = string.Equals(
            Path.GetFullPath(activeRoot),
            Path.GetFullPath(paths.BundledQwenDirectory),
            StringComparison.OrdinalIgnoreCase);
        var baseText = string.IsNullOrWhiteSpace(version) ? "Qwen Code" : $"Qwen Code {version}";
        var text = isBundled ? $"{baseText}（随包内置）" : baseText;

        return string.IsNullOrWhiteSpace(suffix)
            ? text
            : $"{text} · {suffix}";
    }

    /// <summary>
    /// The baseline ships inside the release, so there is nothing to install.
    /// This method validates the active install and returns
    /// <see cref="AcpRuntimeOperationKind.Failed"/> (never a fake no-op
    /// success) when it is missing or corrupt, so the UI can prompt the user
    /// to re-extract / re-download PSX.
    /// </summary>
    public Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Paths;
        var validationError = ValidateActiveInstall(paths, out _);
        if (validationError == null)
        {
            StatusChanged?.Invoke(BuildStatusText());
            return Task.FromResult(new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Qwen Code runtime is bundled and ready."));
        }

        Log($"Qwen Code runtime is invalid: {validationError}");
        var message =
            $"随包内置的 Qwen Code 运行时文件缺失或损坏，请重新解压或重新下载 PSX。({validationError})";
        StatusChanged?.Invoke(message);
        return Task.FromResult(new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed, message));
    }

    /// <summary>
    /// User-requested refresh. Runs a lightweight
    /// <c>npm view @qwen-code/qwen-code@latest version</c> pre-check first:
    /// only a parseable, strictly newer registry version is staged (pinned to
    /// that exact version) into <c>qwen-next</c> — never the directory the
    /// live Agent session is reading from. The staged install must pass
    /// structure validation, an exact-version check and a portable-Node
    /// <c>--version</c> smoke check, and the pointer write must succeed,
    /// before the update is reported as staged; any failure leaves the active
    /// install untouched.
    /// </summary>
    public async Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Paths;
        if (ValidateActiveInstall(paths, out _) != null)
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                "Qwen Code runtime is not installed correctly; skipping refresh.");
        }

        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                "Portable npm is not available; cannot refresh Qwen Code.");
        }

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke(BuildStatusText("正在检查更新"));

            Directory.CreateDirectory(paths.RuntimeRoot);
            var view = await RunNpmAsync(
                paths,
                paths.RuntimeRoot,
                $"npm view {QwenPackageName}@latest version",
                new[] { "view", $"{QwenPackageName}@latest", "version", "--json" },
                cancellationToken).ConfigureAwait(false);
            if (view.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(view.Kind, view.Message, view.ExitCode);
            }

            var candidate = ParseNpmViewVersion(view.Stdout);
            var currentVersion = ReadQwenVersion(ResolveActiveQwenRoot(paths));
            if (!string.IsNullOrWhiteSpace(candidate)
                && !string.IsNullOrWhiteSpace(currentVersion)
                && string.Equals(candidate, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                ClearStaleNext(paths);
                TryWriteActivePointer(paths, ActiveCurrentToken);
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                    $"Qwen Code {currentVersion} 已是最新版本。");
            }

            if (!Version.TryParse(candidate, out var parsedCandidate)
                || !Version.TryParse(currentVersion, out var parsedCurrent))
            {
                Log($"Qwen version comparison is unsafe: registry='{candidate}', current='{currentVersion}'. Not updating.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "无法安全比较 Registry 版本，未执行更新。");
            }

            if (parsedCandidate < parsedCurrent)
            {
                Log($"Registry Qwen {candidate} is older than current {currentVersion}; refusing to downgrade.");
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                    $"Registry 版本（{candidate}）低于当前版本（{currentVersion}），未执行更新。");
            }

            try
            {
                if (Directory.Exists(paths.QwenNextDirectory))
                    Directory.Delete(paths.QwenNextDirectory, recursive: true);
                Directory.CreateDirectory(paths.QwenNextDirectory);
                SeedNextDirectory(paths);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare qwen-next: {ex}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Failed to prepare qwen-next: {ex.Message}");
            }

            var installLabel = $"npm install {QwenPackageName}@{candidate}";
            StatusChanged?.Invoke(BuildStatusText("正在更新 Qwen Code"));
            var install = await RunNpmAsync(
                paths,
                paths.QwenNextDirectory,
                installLabel,
                new[]
                {
                    "install",
                    $"{QwenPackageName}@{candidate}",
                    "--save-exact",
                    "--omit=dev",
                    "--include=optional",
                    "--engine-strict",
                    "--no-audit",
                    "--no-fund"
                },
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(install.Kind, install.Message, install.ExitCode);
            }

            var stagedError = ValidateQwenRoot(paths, paths.QwenNextDirectory, requireLockfile: false, out var stagedEntry);
            if (stagedError != null || stagedEntry == null)
            {
                Log($"Post-update validation failed: {stagedError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Updated Qwen Code is incomplete; not switching to it. ({stagedError})");
            }

            var stagedVersion = ReadQwenVersion(paths.QwenNextDirectory);
            if (!string.Equals(stagedVersion, candidate, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Staged Qwen version '{stagedVersion}' does not match requested '{candidate}'.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Staged Qwen Code version ({stagedVersion}) does not match the requested {candidate}; not switching to it.");
            }

            var smokeError = await RunStagedSmokeCheckAsync(paths, stagedEntry, cancellationToken).ConfigureAwait(false);
            if (smokeError != null)
            {
                Log($"Staged Qwen smoke check failed: {smokeError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Staged Qwen Code failed its start check; not switching to it. ({smokeError})");
            }

            if (!TryWriteActivePointer(paths, ActiveNextToken))
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "已下载更新，但无法写入激活指针，未切换版本。");
            }

            Log($"qwen-next staged at {candidate} and verified; active pointer flipped to 'next'. " +
                "Next PSX launch will use the new version.");
            StatusChanged?.Invoke(BuildStatusText($"已更新到 Qwen Code {candidate}，下次启动生效"));
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Success,
                $"{installLabel} completed successfully.");
        }
        finally
        {
            _npmLock.Release();
        }
    }

    // ---- internals ----

    private string ResolveActiveQwenRoot(RuntimePaths paths)
    {
        return ValidateQwenRoot(paths, paths.QwenCurrentDirectory, requireLockfile: false, out _) == null
            ? paths.QwenCurrentDirectory
            : paths.BundledQwenDirectory;
    }

    private string? ValidateActiveInstall(RuntimePaths paths, out string? entryPath)
    {
        var activeRoot = ResolveActiveQwenRoot(paths);
        var requireLockfile = string.Equals(
            Path.GetFullPath(activeRoot),
            Path.GetFullPath(paths.BundledQwenDirectory),
            StringComparison.OrdinalIgnoreCase);
        return ValidateQwenRoot(paths, activeRoot, requireLockfile, out entryPath);
    }

    private static string? ValidateQwenRoot(
        RuntimePaths paths,
        string qwenRoot,
        bool requireLockfile,
        out string? entryPath)
    {
        entryPath = null;

        if (string.IsNullOrWhiteSpace(paths.PortableNodePath) || !File.Exists(paths.PortableNodePath))
            return "portable Node.js (tools/node/node.exe) is missing";

        if (!Directory.Exists(qwenRoot))
            return $"Qwen directory is missing at {qwenRoot}";

        if (requireLockfile && !File.Exists(Path.Combine(qwenRoot, PackageLockName)))
            return $"{PackageLockName} is missing under tools/qwen";

        var packageDir = Path.Combine(qwenRoot, QwenPackageDirSubpath);
        var packageJsonPath = Path.Combine(qwenRoot, QwenPackageJsonSubpath);
        if (!File.Exists(packageJsonPath))
            return $"{QwenPackageName} package.json is missing under {qwenRoot}";

        string? binRelative;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            binRelative = ExtractBinPath(document.RootElement);
        }
        catch (Exception ex)
        {
            return $"failed to parse {QwenPackageName} package.json: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(binRelative))
            return $"{QwenPackageName} package.json does not declare a 'bin.qwen' entry";

        var packageDirFull = Path.GetFullPath(packageDir);
        var resolvedEntry = Path.GetFullPath(Path.Combine(packageDirFull, binRelative));
        var packageDirPrefix = packageDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? packageDirFull
            : packageDirFull + Path.DirectorySeparatorChar;
        if (!resolvedEntry.StartsWith(packageDirPrefix, StringComparison.OrdinalIgnoreCase))
            return $"'bin.qwen' path '{binRelative}' escapes the Qwen package directory";

        if (!File.Exists(resolvedEntry))
            return $"resolved Qwen entry point is missing at {resolvedEntry}";

        entryPath = resolvedEntry;
        return null;
    }

    private static string EnsureQwenSystemSettingsFile(RuntimePaths paths)
    {
        Directory.CreateDirectory(paths.RuntimeRoot);
        var settingsPath = Path.Combine(paths.RuntimeRoot, QwenSystemSettingsFileName);
        if (File.Exists(settingsPath))
        {
            try
            {
                var existing = File.ReadAllText(settingsPath);
                if (HasDisabledAutoUpdate(existing))
                    return settingsPath;
            }
            catch (IOException)
            {
                // Fall through and rewrite.
            }
        }

        var tmp = settingsPath + ".tmp";
        File.WriteAllText(tmp, QwenSystemSettingsContent, Encoding.UTF8);
        File.Move(tmp, settingsPath, overwrite: true);
        return settingsPath;
    }

    private static bool HasDisabledAutoUpdate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("general", out var general)
                || general.ValueKind != JsonValueKind.Object
                || !general.TryGetProperty("enableAutoUpdate", out var flag)
                || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            return flag.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a PowerShell invocation for the interactive Qwen CLI using the
    /// resolved portable Node + package entry (not a PATH-dependent <c>qwen</c>).
    /// Returns null when the install is incomplete.
    /// </summary>
    public string? TryBuildInteractivePowerShellInvocation(string? sessionId)
    {
        var paths = Paths;
        var validationError = ValidateActiveInstall(paths, out var entryPath);
        if (validationError != null || entryPath == null || string.IsNullOrWhiteSpace(paths.PortableNodePath))
            return null;

        var command = new StringBuilder();
        command.Append("& ").Append(QuoteForPowerShell(paths.PortableNodePath));
        command.Append(' ').Append(QuoteForPowerShell(entryPath));
        if (!string.IsNullOrWhiteSpace(sessionId))
            command.Append(" --resume ").Append(QuoteForPowerShell(sessionId));
        return command.ToString();
    }

    private static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";

    private void PromoteNextToCurrent(RuntimePaths paths)
    {
        var backup = paths.QwenCurrentDirectory + ".old";
        try
        {
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);

            if (Directory.Exists(paths.QwenCurrentDirectory))
                Directory.Move(paths.QwenCurrentDirectory, backup);

            Directory.Move(paths.QwenNextDirectory, paths.QwenCurrentDirectory);
            Directory.Delete(backup, recursive: true);
        }
        catch (Exception ex)
        {
            Log($"Qwen promote failed mid-swap: {ex}. Recovering.");
            if (Directory.Exists(backup) && !Directory.Exists(paths.QwenCurrentDirectory))
            {
                try { Directory.Move(backup, paths.QwenCurrentDirectory); } catch { /* give up */ }
            }
            TryWriteActivePointer(paths, ActiveCurrentToken);
            return;
        }

        TryWriteActivePointer(paths, ActiveCurrentToken);
        Log("Qwen promote: qwen-next is now qwen-current.");
        StatusChanged?.Invoke(BuildStatusText());
    }

    private void DropRuntimeCopyWhenBundledIsSameOrNewer(RuntimePaths paths)
    {
        if (ValidateQwenRoot(paths, paths.QwenCurrentDirectory, requireLockfile: false, out _) != null)
            return;
        if (ValidateQwenRoot(paths, paths.BundledQwenDirectory, requireLockfile: true, out _) != null)
            return;

        var bundledVersion = ReadQwenVersion(paths.BundledQwenDirectory);
        var currentVersion = ReadQwenVersion(paths.QwenCurrentDirectory);
        if (!IsSameOrNewer(bundledVersion, currentVersion))
            return;

        Log($"Bundled Qwen {bundledVersion} supersedes self-updated {currentVersion}; dropping runtime copies.");
        foreach (var directory in new[] { paths.QwenCurrentDirectory, paths.QwenNextDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                Log($"WARN: could not drop {directory}: {ex.Message}");
            }
        }
        TryWriteActivePointer(paths, ActiveCurrentToken);
    }

    private static bool IsSameOrNewer(string? candidate, string? baseline)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(baseline))
            return false;
        if (string.Equals(candidate, baseline, StringComparison.OrdinalIgnoreCase))
            return true;
        return Version.TryParse(candidate, out var parsedCandidate)
            && Version.TryParse(baseline, out var parsedBaseline)
            && parsedCandidate >= parsedBaseline;
    }

    private void SeedNextDirectory(RuntimePaths paths)
    {
        foreach (var name in new[] { "package.json", ".npmrc" })
        {
            var source = Path.Combine(paths.BundledQwenDirectory, name);
            if (!File.Exists(source))
                continue;
            var dest = Path.Combine(paths.QwenNextDirectory, name);
            File.Copy(source, dest, overwrite: true);
            Log($"Seeded {name} -> {dest}");
        }
    }

    private void ClearStaleNext(RuntimePaths paths)
    {
        try
        {
            if (Directory.Exists(paths.QwenNextDirectory))
                Directory.Delete(paths.QwenNextDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Log($"WARN: could not clear stale qwen-next: {ex.Message}");
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

    private sealed record NpmRunOutcome(
        AcpRuntimeOperationKind Kind,
        string Message,
        int? ExitCode,
        string Stdout);

    private async Task<NpmRunOutcome> RunNpmAsync(
        RuntimePaths paths,
        string workingDirectory,
        string label,
        string[] arguments,
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
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add(paths.PortableNpmCliPath!);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Log($"Starting: {label} in {workingDirectory}");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log($"Failed to start {label}: {ex}");
            return new NpmRunOutcome(AcpRuntimeOperationKind.Failed,
                $"Failed to start {label}: {ex.Message}", null, "");
        }

        if (process == null)
        {
            return new NpmRunOutcome(AcpRuntimeOperationKind.Failed,
                $"{label} did not start.", null, "");
        }
        using var processLifetime = process;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_processTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            Log($"{label} timed out after {_processTimeout.TotalMinutes:0.##} minutes.");
            return new NpmRunOutcome(AcpRuntimeOperationKind.Failed,
                $"{label} timed out.", null, "");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            Log($"{label} was cancelled.");
            return new NpmRunOutcome(AcpRuntimeOperationKind.Cancelled,
                $"{label} was cancelled.", null, "");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var exitCode = process.ExitCode;

        if (!string.IsNullOrWhiteSpace(stdout))
            Log($"stdout:\n{stdout.Trim()}");
        if (!string.IsNullOrWhiteSpace(stderr))
            Log($"stderr:\n{stderr.Trim()}");
        Log($"{label} exited with code {exitCode}.");

        if (exitCode != 0)
        {
            var kind = LooksLikeNetworkError(stderr)
                ? AcpRuntimeOperationKind.NetworkUnavailable
                : AcpRuntimeOperationKind.Failed;
            return new NpmRunOutcome(kind,
                $"{label} failed with exit code {exitCode}.", exitCode, stdout);
        }

        return new NpmRunOutcome(AcpRuntimeOperationKind.Success,
            $"{label} completed successfully.", exitCode, stdout);
    }

    private async Task<string?> RunStagedSmokeCheckAsync(
        RuntimePaths paths,
        string entryPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.PortableNodePath,
            WorkingDirectory = Path.GetDirectoryName(entryPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(entryPath);
        startInfo.ArgumentList.Add("--version");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return $"failed to start the staged entry: {ex.Message}";
        }
        if (process == null)
            return "the staged entry process did not start";
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
            TryKill(process);
            return $"--version did not finish within {SmokeCheckTimeout.TotalSeconds:0} seconds";
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            Log($"Staged smoke check stderr:\n{stderr}");
            return $"--version exited with code {process.ExitCode}";
        }

        Log($"Staged smoke check passed: --version -> {stdout}");
        return null;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private static bool LooksLikeNetworkError(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        var lowered = stderr.ToLowerInvariant();
        return lowered.Contains("etimedout")
            || lowered.Contains("enotfound")
            || lowered.Contains("econnrefused")
            || lowered.Contains("network")
            || lowered.Contains("registry.npmjs.org")
            || lowered.Contains("getaddrinfo");
    }

    private bool PointerSaysNext(RuntimePaths paths)
    {
        try
        {
            if (!File.Exists(paths.QwenActivePointerFile))
                return false;
            var token = File.ReadAllText(paths.QwenActivePointerFile).Trim();
            return string.Equals(token, ActiveNextToken, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool TryWriteActivePointer(RuntimePaths paths, string token)
    {
        try
        {
            Directory.CreateDirectory(paths.RuntimeRoot);
            var tmp = paths.QwenActivePointerFile + ".tmp";
            File.WriteAllText(tmp, token);
            File.Move(tmp, paths.QwenActivePointerFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to write Qwen active pointer '{token}': {ex}");
            return false;
        }
    }

    /// <summary>
    /// Extracts the Qwen bin entry from a package.json root. Supports both the
    /// object form (<c>"bin": { "qwen": "dist/main.mjs" }</c>) and the string
    /// form (<c>"bin": "dist/main.mjs"</c>).
    /// </summary>
    private static string? ExtractBinPath(JsonElement root)
    {
        if (!root.TryGetProperty("bin", out var bin))
            return null;

        if (bin.ValueKind == JsonValueKind.String)
            return bin.GetString();

        if (bin.ValueKind == JsonValueKind.Object)
        {
            if (bin.TryGetProperty("qwen", out var qwenBin) && qwenBin.ValueKind == JsonValueKind.String)
                return qwenBin.GetString();

            foreach (var property in bin.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ReadQwenVersion(string qwenRoot)
    {
        try
        {
            var packageJsonPath = Path.Combine(qwenRoot, QwenPackageJsonSubpath);
            if (!File.Exists(packageJsonPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                return version.GetString();
            }
        }
        catch
        {
            // Version display is best-effort.
        }

        return null;
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

    public void Dispose()
    {
        _npmLock.Dispose();
    }
}
