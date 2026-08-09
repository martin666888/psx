using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Runtime for the Kimi Code CLI. The baseline install ships inside the PSX
/// release at <c>{InstallDir}/tools/kimi/</c> (pre-installed at build time;
/// Kimi Code is MIT-licensed). On top of that baseline the runtime supports
/// the same two-directory self-update model as <see cref="AcpRuntimeManager"/>:
/// background <c>npm install @moonshot-ai/kimi-code@latest</c> stages into the
/// writable <c>runtime/kimi-next/</c>, a pointer file flips to "next", and the
/// next PSX launch promotes it to <c>runtime/kimi-current/</c>. A structurally
/// valid <c>kimi-current</c> takes precedence over the bundled copy; deleting
/// it (or shipping a newer bundled version with a PSX release) falls back to
/// the bundled baseline.
///
/// Kimi speaks ACP natively via <c>node &lt;entry&gt; acp</c> over stdio. The entry
/// point is resolved from the package's <c>bin.kimi</c> field (expected to be
/// <c>dist/main.mjs</c>), with a guard preventing the bin path from escaping the
/// package directory.
/// </summary>
public sealed class KimiCodeAcpRuntime : IAcpAgentRuntime
{
    private const string KimiPackageName = "@moonshot-ai/kimi-code";
    private const string PackageLockName = "package-lock.json";
    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";

    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Staged-entry <c>--version</c> smoke check budget.</summary>
    private static readonly TimeSpan SmokeCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string KimiPackageDirSubpath =
        Path.Combine("node_modules", "@moonshot-ai", "kimi-code");
    private static readonly string KimiPackageJsonSubpath =
        Path.Combine(KimiPackageDirSubpath, "package.json");

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;
    private readonly TimeSpan _processTimeout;
    private readonly NpmRuntimeProcessRunner _npmRunner;
    private readonly StagedRuntimeStore _stagedStore;

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

    public KimiCodeAcpRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, DefaultProcessTimeout)
    {
    }

    internal KimiCodeAcpRuntime(RuntimeLocator locator, string logDirectory, TimeSpan processTimeout)
    {
        if (processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processTimeout = processTimeout;
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "kimi-runtime.log");
        _npmRunner = new NpmRuntimeProcessRunner(_processTimeout, Log);
        _stagedStore = new StagedRuntimeStore("Kimi", Log);
    }

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    /// <summary>Kimi Code self-updates through the kimi-current/kimi-next staging model.</summary>
    public bool SupportsSelfUpdate => true;

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        var paths = Paths;
        var currentVersion = ReadKimiVersion(ResolveActiveKimiRoot(paths));
        string? pendingVersion = null;
        if (PointerSaysNext(paths)
            && ValidateKimiRoot(paths, paths.KimiNextDirectory, requireLockfile: false, out _) == null)
        {
            pendingVersion = ReadKimiVersion(paths.KimiNextDirectory);
        }

        var hasPending = pendingVersion != null
            && !string.Equals(pendingVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        // The Kimi package version IS the product version, so TechnicalDetails
        // stays null (no duplicate display in the toolbar tooltip).
        return new RuntimeVersionSnapshot(
            CurrentVersion: currentVersion,
            PendingVersion: hasPending ? pendingVersion : null,
            HasPendingUpdate: hasPending)
        {
            ProductName = "Kimi Code"
        };
    }

    /// <summary>
    /// True only when the active install is structurally valid: portable
    /// Node, the Kimi package manifest, a resolvable <c>bin.kimi</c> entry
    /// that stays inside the package directory, and the resolved entry file
    /// itself (the bundled baseline additionally requires its pinned
    /// lockfile). Missing Git Bash does NOT make this false — that is a
    /// separate Windows prerequisite surfaced via status text.
    /// </summary>
    public bool IsReady()
    {
        var paths = Paths;
        return ValidateActiveInstall(paths, out _) == null;
    }

    /// <summary>
    /// Startup promote, mirroring <see cref="AcpRuntimeManager.TryPromoteNextToCurrentAsync"/>:
    /// when the pointer says "next" and the staged directory is valid, swap it
    /// into <c>kimi-current</c>. Afterwards, if a PSX release shipped a bundled
    /// Kimi that is the same or newer than the self-updated copy, drop the
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
                if (ValidateKimiRoot(paths, paths.KimiNextDirectory, requireLockfile: false, out _) != null)
                {
                    Log("Kimi promote aborted: kimi-next is incomplete; reverting pointer to 'current'.");
                    TryWriteActivePointer(paths, ActiveCurrentToken);
                }
                else
                {
                    PromoteNextToCurrent(paths);
                }
            }
            else if (File.Exists(paths.KimiActivePointerFile))
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
                ?? $"Kimi Code runtime is not installed correctly under {paths.BundledKimiDirectory}. " +
                   "Re-extract or re-download PSX.");
        }

        var nodePath = paths.PortableNodePath;
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new InvalidOperationException(
                "Portable Node.js was not found next to PSX.exe. The release zip should include tools/node/node.exe.");
        }

        // Pass through the Kimi credential/shell overrides and PATH so Kimi can
        // find Git Bash and its ~/.kimi-code credential store. null values are
        // ignored by the transport (they mean "remove"), so only forward vars
        // that are actually set in this process.
        var environment = new Dictionary<string, string?>();
        ForwardEnvironmentVariable(environment, "KIMI_CODE_HOME");
        ForwardEnvironmentVariable(environment, "KIMI_SHELL_PATH");
        ForwardEnvironmentVariable(environment, "PATH");

        return new AcpProcessSpec
        {
            FileName = nodePath,
            WorkingDirectory = workingDirectory,
            Arguments = new[] { entryPath, "acp" },
            Environment = environment
        };
    }

    public string BuildStatusText(string? suffix = null)
    {
        var paths = Paths;
        var activeRoot = ResolveActiveKimiRoot(paths);
        var version = ReadKimiVersion(activeRoot);
        var isBundled = string.Equals(
            Path.GetFullPath(activeRoot),
            Path.GetFullPath(paths.BundledKimiDirectory),
            StringComparison.OrdinalIgnoreCase);
        var baseText = string.IsNullOrWhiteSpace(version) ? "Kimi Code" : $"Kimi Code {version}";
        var text = isBundled ? $"{baseText}（随包内置）" : baseText;

        if (!IsGitBashAvailable())
            text = $"{text} · 缺少 Kimi Code Windows 前置条件（未检测到 Git Bash）";

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
                "Kimi Code runtime is bundled and ready."));
        }

        Log($"Kimi Code runtime is invalid: {validationError}");
        var message =
            $"随包内置的 Kimi Code 运行时文件缺失或损坏，请重新解压或重新下载 PSX。({validationError})";
        StatusChanged?.Invoke(message);
        return Task.FromResult(new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed, message));
    }

    /// <summary>
    /// User-requested refresh. Runs a lightweight
    /// <c>npm view @moonshot-ai/kimi-code@latest version</c> pre-check first:
    /// only a parseable, strictly newer registry version is staged (pinned to
    /// that exact version) into <c>kimi-next</c> — never the directory the
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
                "Kimi Code runtime is not installed correctly; skipping refresh.");
        }

        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                "Portable npm is not available; cannot refresh Kimi Code.");
        }

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke(BuildStatusText("正在检查更新"));

            // The registry pre-check runs from RuntimeRoot, which may not
            // exist yet on a first run — a missing working directory fails
            // process start outright.
            Directory.CreateDirectory(paths.RuntimeRoot);
            var view = await RunNpmAsync(
                paths,
                paths.RuntimeRoot,
                $"npm view {KimiPackageName}@latest version",
                new[] { "view", $"{KimiPackageName}@latest", "version", "--json" },
                cancellationToken).ConfigureAwait(false);
            if (view.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(view.Kind, view.Message, view.ExitCode);
            }

            var candidate = ParseNpmViewVersion(view.Stdout);
            var currentVersion = ReadKimiVersion(ResolveActiveKimiRoot(paths));
            if (!string.IsNullOrWhiteSpace(candidate)
                && !string.IsNullOrWhiteSpace(currentVersion)
                && string.Equals(candidate, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                ClearStaleNext(paths);
                TryWriteActivePointer(paths, ActiveCurrentToken);
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                    $"Kimi Code {currentVersion} 已是最新版本。");
            }

            // Unsafe comparisons must fail loudly: the coordinator maps
            // AlreadyReady to "Up to date", which would disguise "cannot
            // tell" as "already newest".
            if (!Version.TryParse(candidate, out var parsedCandidate)
                || !Version.TryParse(currentVersion, out var parsedCurrent))
            {
                Log($"Kimi version comparison is unsafe: registry='{candidate}', current='{currentVersion}'. Not updating.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "无法安全比较 Registry 版本，未执行更新。");
            }

            if (parsedCandidate < parsedCurrent)
            {
                Log($"Registry Kimi {candidate} is older than current {currentVersion}; refusing to downgrade.");
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                    $"Registry 版本（{candidate}）低于当前版本（{currentVersion}），未执行更新。");
            }

            try
            {
                if (Directory.Exists(paths.KimiNextDirectory))
                    Directory.Delete(paths.KimiNextDirectory, recursive: true);
                Directory.CreateDirectory(paths.KimiNextDirectory);
                SeedNextDirectory(paths);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare kimi-next: {ex}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Failed to prepare kimi-next: {ex.Message}");
            }

            // Pin the exact version the pre-check saw so "latest" cannot
            // drift between view and install. --engine-strict turns an
            // engines.node mismatch into a hard error instead of npm's
            // default warning, protecting the portable Node.
            var installLabel = $"npm install {KimiPackageName}@{candidate}";
            StatusChanged?.Invoke(BuildStatusText("正在更新 Kimi Code"));
            var install = await RunNpmAsync(
                paths,
                paths.KimiNextDirectory,
                installLabel,
                new[]
                {
                    "install",
                    $"{KimiPackageName}@{candidate}",
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

            // ① Structure: manifest, bin entry and entry file all in place.
            var stagedError = ValidateKimiRoot(paths, paths.KimiNextDirectory, requireLockfile: false, out var stagedEntry);
            if (stagedError != null || stagedEntry == null)
            {
                Log($"Post-update validation failed: {stagedError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Updated Kimi Code is incomplete; not switching to it. ({stagedError})");
            }

            // ② The staged install must be exactly the version we asked for.
            var stagedVersion = ReadKimiVersion(paths.KimiNextDirectory);
            if (!string.Equals(stagedVersion, candidate, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Staged Kimi version '{stagedVersion}' does not match requested '{candidate}'.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Staged Kimi Code version ({stagedVersion}) does not match the requested {candidate}; not switching to it.");
            }

            // ③ The staged entry must actually start on the portable Node.
            var smokeError = await RunStagedSmokeCheckAsync(paths, stagedEntry, cancellationToken).ConfigureAwait(false);
            if (smokeError != null)
            {
                Log($"Staged Kimi smoke check failed: {smokeError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Staged Kimi Code failed its start check; not switching to it. ({smokeError})");
            }

            // ④ The pointer write is the activation step and must be
            // observable: a swallowed failure here would report a staged
            // update that never applies.
            if (!TryWriteActivePointer(paths, ActiveNextToken))
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "已下载更新，但无法写入激活指针，未切换版本。");
            }

            Log($"kimi-next staged at {candidate} and verified; active pointer flipped to 'next'. " +
                "Next PSX launch will use the new version.");
            StatusChanged?.Invoke(BuildStatusText($"已更新到 Kimi Code {candidate}，下次启动生效"));
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Success,
                $"{installLabel} completed successfully.");
        }
        finally
        {
            _npmLock.Release();
        }
    }

    // ---- internals ----

    /// <summary>
    /// The install the current process should load: a structurally valid
    /// self-updated <c>kimi-current</c> wins; otherwise the bundled baseline.
    /// </summary>
    private string ResolveActiveKimiRoot(RuntimePaths paths)
    {
        return ValidateKimiRoot(paths, paths.KimiCurrentDirectory, requireLockfile: false, out _) == null
            ? paths.KimiCurrentDirectory
            : paths.BundledKimiDirectory;
    }

    private string? ValidateActiveInstall(RuntimePaths paths, out string? entryPath)
    {
        var activeRoot = ResolveActiveKimiRoot(paths);
        var requireLockfile = string.Equals(
            Path.GetFullPath(activeRoot),
            Path.GetFullPath(paths.BundledKimiDirectory),
            StringComparison.OrdinalIgnoreCase);
        return ValidateKimiRoot(paths, activeRoot, requireLockfile, out entryPath);
    }

    /// <summary>
    /// Validates one Kimi install root. Returns <c>null</c> when everything is
    /// present and consistent; otherwise a human-readable reason. On success,
    /// <paramref name="entryPath"/> is the absolute path to the ACP entry file.
    /// The pinned lockfile is only required for the bundled baseline (its
    /// absence there indicates a broken unzip); self-updated staging installs
    /// resolve @latest and may rewrite their lockfile freely.
    /// </summary>
    private static string? ValidateKimiRoot(
        RuntimePaths paths,
        string kimiRoot,
        bool requireLockfile,
        out string? entryPath)
    {
        entryPath = null;

        if (string.IsNullOrWhiteSpace(paths.PortableNodePath) || !File.Exists(paths.PortableNodePath))
            return "portable Node.js (tools/node/node.exe) is missing";

        if (!Directory.Exists(kimiRoot))
            return $"Kimi directory is missing at {kimiRoot}";

        if (requireLockfile && !File.Exists(Path.Combine(kimiRoot, PackageLockName)))
            return $"{PackageLockName} is missing under tools/kimi";

        var packageDir = Path.Combine(kimiRoot, KimiPackageDirSubpath);
        var packageJsonPath = Path.Combine(kimiRoot, KimiPackageJsonSubpath);
        if (!File.Exists(packageJsonPath))
            return $"{KimiPackageName} package.json is missing under {kimiRoot}";

        string? binRelative;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            binRelative = ExtractBinPath(document.RootElement);
        }
        catch (Exception ex)
        {
            return $"failed to parse {KimiPackageName} package.json: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(binRelative))
            return $"{KimiPackageName} package.json does not declare a 'bin.kimi' entry";

        // Resolve the entry point and guard against a bin path escaping the
        // package directory (e.g. "../../evil.js" or an absolute path).
        var packageDirFull = Path.GetFullPath(packageDir);
        var resolvedEntry = Path.GetFullPath(Path.Combine(packageDirFull, binRelative));
        var packageDirPrefix = packageDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? packageDirFull
            : packageDirFull + Path.DirectorySeparatorChar;
        if (!resolvedEntry.StartsWith(packageDirPrefix, StringComparison.OrdinalIgnoreCase))
            return $"'bin.kimi' path '{binRelative}' escapes the Kimi package directory";

        if (!File.Exists(resolvedEntry))
            return $"resolved Kimi entry point is missing at {resolvedEntry}";

        entryPath = resolvedEntry;
        return null;
    }

    private void PromoteNextToCurrent(RuntimePaths paths)
    {
        if (_stagedStore.PromoteNextToCurrent(
                paths.RuntimeRoot,
                paths.KimiCurrentDirectory,
                paths.KimiNextDirectory,
                paths.KimiActivePointerFile,
                ActiveCurrentToken))
        {
            StatusChanged?.Invoke(BuildStatusText());
        }
    }
    private void DropRuntimeCopyWhenBundledIsSameOrNewer(RuntimePaths paths)
    {
        if (ValidateKimiRoot(paths, paths.KimiCurrentDirectory, requireLockfile: false, out _) != null)
            return;
        if (ValidateKimiRoot(paths, paths.BundledKimiDirectory, requireLockfile: true, out _) != null)
            return;

        var bundledVersion = ReadKimiVersion(paths.BundledKimiDirectory);
        var currentVersion = ReadKimiVersion(paths.KimiCurrentDirectory);
        if (!IsSameOrNewer(bundledVersion, currentVersion))
            return;

        Log($"Bundled Kimi {bundledVersion} supersedes self-updated {currentVersion}; dropping runtime copies.");
        _stagedStore.DropRuntimeCopies(paths.KimiCurrentDirectory, paths.KimiNextDirectory);
        TryWriteActivePointer(paths, ActiveCurrentToken);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the same as or newer than
    /// <paramref name="baseline"/>. Unparseable versions compare by equality
    /// only, so prerelease-style versions never trigger a destructive drop.
    /// </summary>
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
        // The bundled install doubles as the refresh seed. Copy package.json
        // and .npmrc only — omitting the pinned lockfile lets npm resolve the
        // pinned candidate version freshly.
        foreach (var name in new[] { "package.json", ".npmrc" })
        {
            var source = Path.Combine(paths.BundledKimiDirectory, name);
            if (!File.Exists(source))
                continue;
            var dest = Path.Combine(paths.KimiNextDirectory, name);
            File.Copy(source, dest, overwrite: true);
            Log($"Seeded {name} -> {dest}");
        }
    }

    private void ClearStaleNext(RuntimePaths paths)
        => _stagedStore.ClearStaleNext(paths.KimiNextDirectory);
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

    private Task<NpmRuntimeProcessResult> RunNpmAsync(
        RuntimePaths paths,
        string workingDirectory,
        string label,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        return _npmRunner.RunAsync(
            paths.PortableNodePath!,
            paths.PortableNpmCliPath!,
            workingDirectory,
            label,
            arguments,
            cancellationToken);
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
            await RuntimeProcessCleanup.TerminateAndDrainAsync(
                process, stdoutTask, stderrTask, Log).ConfigureAwait(false);
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

    private bool PointerSaysNext(RuntimePaths paths)
        => _stagedStore.PointerSaysNext(paths.KimiActivePointerFile, ActiveNextToken);
    private bool TryWriteActivePointer(RuntimePaths paths, string token)
        => _stagedStore.TryWriteActivePointer(paths.RuntimeRoot, paths.KimiActivePointerFile, token);
    private static string? ExtractBinPath(JsonElement root)
    {
        if (!root.TryGetProperty("bin", out var bin))
            return null;

        if (bin.ValueKind == JsonValueKind.String)
            return bin.GetString();

        if (bin.ValueKind == JsonValueKind.Object)
        {
            if (bin.TryGetProperty("kimi", out var kimiBin) && kimiBin.ValueKind == JsonValueKind.String)
                return kimiBin.GetString();

            // Fall back to the first declared bin if "kimi" is absent.
            foreach (var property in bin.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ReadKimiVersion(string kimiRoot)
    {
        try
        {
            var packageJsonPath = Path.Combine(kimiRoot, KimiPackageJsonSubpath);
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

    /// <summary>
    /// Kimi runs shell tool calls through Git Bash on Windows. It is available
    /// when <c>KIMI_SHELL_PATH</c> points at an existing file, or a Git for
    /// Windows <c>bash.exe</c> can be found in the usual locations / on PATH.
    /// </summary>
    private static bool IsGitBashAvailable()
    {
        var shellOverride = Environment.GetEnvironmentVariable("KIMI_SHELL_PATH");
        if (!string.IsNullOrWhiteSpace(shellOverride) && File.Exists(shellOverride))
            return true;

        var candidates = new List<string>();
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "LOCALAPPDATA" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "Git", "bin", "bash.exe"));
                candidates.Add(Path.Combine(root, "Git", "usr", "bin", "bash.exe"));
                candidates.Add(Path.Combine(root, "Programs", "Git", "bin", "bash.exe"));
            }
        }

        if (candidates.Any(File.Exists))
            return true;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), "bash.exe")))
                        return true;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return false;
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
