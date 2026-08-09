using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Helpers;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Runtime for the OpenCode CLI. The baseline install ships inside the PSX
/// release at <c>{InstallDir}/tools/opencode/</c> (pre-installed at build
/// time). On top of that baseline the runtime supports the same two-directory
/// self-update model as <see cref="QwenCodeAcpRuntime"/>: a user-requested
/// refresh stages into the writable <c>runtime/opencode-next/</c>, a pointer
/// file flips to "next", and the next PSX launch promotes it to
/// <c>runtime/opencode-current/</c>. A structurally valid
/// <c>opencode-current</c> takes precedence over the bundled copy; deleting it
/// (or shipping a newer bundled version with a PSX release) falls back to the
/// bundled baseline.
///
/// OpenCode is a native Bun-compiled single-file executable, not a Node
/// script: the ACP process is launched as <c>opencode.exe acp</c> over stdio
/// and does not need the portable Node runtime (Node is only used to drive
/// npm during install/update). The release depends on the platform package
/// <c>opencode-windows-x64</c> directly — never the <c>opencode-ai</c> wrapper,
/// whose bin is a 479-byte placeholder hydrated by a postinstall script.
///
/// The modern binary requires an AVX2-capable CPU. On machines without AVX2
/// the bundled copy is unusable, so the runtime instead installs
/// <c>opencode-windows-x64-baseline</c> into <c>opencode-current</c> after
/// explicit user confirmation (the same "install card" flow as Qoder), and
/// refreshes keep selecting the baseline variant. The "drop runtime copy when
/// bundled is same-or-newer" cleanup only runs when the bundled binary can
/// actually execute on this machine (AVX2 present) — otherwise every PSX
/// upgrade would tear down a working baseline install.
/// </summary>
public sealed class OpencodeAcpRuntime : IAcpAgentRuntime
{
    /// <summary>Variant installed by the release / selected on AVX2 CPUs.</summary>
    internal const string ModernPackageName = "opencode-windows-x64";

    /// <summary>Variant selected on CPUs without AVX2; never shipped in the zip.</summary>
    internal const string BaselinePackageName = "opencode-windows-x64-baseline";

    /// <summary>Oldest registry version Refresh is allowed to stage.</summary>
    internal const string MinimumCompatibleVersion = "1.18.14";

    private const string PackageLockName = "package-lock.json";
    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";

    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Staged-binary <c>--version</c> smoke check budget.</summary>
    private static readonly TimeSpan SmokeCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] VariantPackageNames = { ModernPackageName, BaselinePackageName };
    private static readonly string[] BaselineFirstVariantPackageNames = { BaselinePackageName, ModernPackageName };

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;
    private readonly TimeSpan _processTimeout;
    private readonly Func<bool> _supportsAvx2;

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

    public OpencodeAcpRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, DefaultProcessTimeout)
    {
    }

    internal OpencodeAcpRuntime(
        RuntimeLocator locator,
        string logDirectory,
        TimeSpan processTimeout,
        Func<bool>? supportsAvx2 = null)
    {
        if (processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processTimeout = processTimeout;
        _supportsAvx2 = supportsAvx2
            ?? (() => NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_AVX2_INSTRUCTIONS_AVAILABLE));
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "opencode-runtime.log");
    }

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    /// <summary>OpenCode self-updates through the opencode-current/opencode-next staging model.</summary>
    public bool SupportsSelfUpdate => true;

    /// <summary>True when this CPU can execute the modern (bundled) binary variant.</summary>
    internal bool SupportsAvx2 => _supportsAvx2();

    /// <summary>The platform package this machine installs during refresh / baseline install.</summary>
    internal string VariantPackageName => SupportsAvx2 ? ModernPackageName : BaselinePackageName;

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        var paths = Paths;
        var currentVersion = ReadOpencodeVersion(ResolveActiveOpencodeRoot(paths));
        string? pendingVersion = null;
        if (PointerSaysNext(paths)
            && ValidateOpencodeRoot(paths, paths.OpencodeNextDirectory, requireLockfile: false, out _) == null)
        {
            pendingVersion = ReadOpencodeVersion(paths.OpencodeNextDirectory);
        }

        var hasPending = pendingVersion != null
            && !string.Equals(pendingVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        return new RuntimeVersionSnapshot(
            CurrentVersion: currentVersion,
            PendingVersion: hasPending ? pendingVersion : null,
            HasPendingUpdate: hasPending)
        {
            ProductName = "OpenCode"
        };
    }

    /// <summary>
    /// True only when the active install is structurally valid: a variant
    /// package manifest (<c>opencode-windows-x64</c> or
    /// <c>opencode-windows-x64-baseline</c>) and its <c>bin/opencode.exe</c>
    /// (the bundled baseline additionally requires its pinned lockfile and an
    /// AVX2-capable CPU, since the bundled binary is the modern variant).
    /// </summary>
    public bool IsReady()
    {
        var paths = Paths;
        return ValidateActiveInstall(paths, out _) == null;
    }

    /// <summary>
    /// Startup promote, mirroring <see cref="QwenCodeAcpRuntime"/>: when the
    /// pointer says "next" and the staged directory is valid, swap it into
    /// <c>opencode-current</c>. Afterwards, if a PSX release shipped a bundled
    /// OpenCode that is the same or newer than the self-updated copy, drop the
    /// runtime copies and fall back to the bundled baseline — but only when
    /// this machine can actually run the bundled (modern) binary; on
    /// AVX2-less machines the runtime copy is the only usable install.
    /// </summary>
    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;
            if (PointerSaysNext(paths))
            {
                if (ValidateOpencodeRoot(paths, paths.OpencodeNextDirectory, requireLockfile: false, out _) != null)
                {
                    Log("OpenCode promote aborted: opencode-next is incomplete; reverting pointer to 'current'.");
                    TryWriteActivePointer(paths, ActiveCurrentToken);
                }
                else
                {
                    PromoteNextToCurrent(paths);
                }
            }
            else if (File.Exists(paths.OpencodeActivePointerFile))
            {
                TryWriteActivePointer(paths, ActiveCurrentToken);
            }

            if (SupportsAvx2)
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
        var validationError = ValidateActiveInstall(paths, out var exePath);
        if (validationError != null || exePath == null)
        {
            throw new InvalidOperationException(
                validationError
                ?? $"OpenCode runtime is not installed correctly under {paths.BundledOpencodeDirectory}. " +
                   "Re-extract or re-download PSX.");
        }

        // Disable OpenCode's built-in auto-update so the runtime directory
        // stays PSX-managed. The config `autoupdate: false` knob is ignored by
        // several upstream versions (anomalyco/opencode#3412, #6984, #21072),
        // so the environment variable is the only reliable switch. Forward
        // PATH only when present in this process.
        var environment = new Dictionary<string, string?>
        {
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "true"
        };
        ForwardEnvironmentVariable(environment, "PATH");

        return new AcpProcessSpec
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory,
            Arguments = new[] { "acp" },
            Environment = environment
        };
    }

    public string BuildStatusText(string? suffix = null)
    {
        var paths = Paths;
        var activeRoot = ResolveActiveOpencodeRoot(paths);
        var version = ReadOpencodeVersion(activeRoot);
        var isBundled = string.Equals(
            Path.GetFullPath(activeRoot),
            Path.GetFullPath(paths.BundledOpencodeDirectory),
            StringComparison.OrdinalIgnoreCase);
        var baseText = string.IsNullOrWhiteSpace(version) ? "OpenCode" : $"OpenCode {version}";
        var text = isBundled ? $"{baseText}（随包内置）" : baseText;

        return string.IsNullOrWhiteSpace(suffix)
            ? text
            : $"{text} · {suffix}";
    }

    /// <summary>
    /// On AVX2-capable machines the baseline ships inside the release, so
    /// there is nothing to install: this validates the active install and
    /// returns <see cref="AcpRuntimeOperationKind.Failed"/> (never a fake
    /// no-op success) when it is missing or corrupt, so the UI can prompt the
    /// user to re-extract / re-download PSX.
    ///
    /// On machines without AVX2 the bundled modern binary cannot run; after
    /// explicit user confirmation this installs the baseline variant (pinned
    /// to the bundled version, or <see cref="MinimumCompatibleVersion"/> when
    /// no bundled install exists) into <c>opencode-current</c>.
    /// </summary>
    public async Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Paths;
        var validationError = ValidateActiveInstall(paths, out _);
        if (validationError == null)
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "OpenCode runtime is bundled and ready.");
        }

        if (SupportsAvx2)
        {
            Log($"OpenCode runtime is invalid: {validationError}");
            var avx2Message =
                $"随包内置的 OpenCode 运行时文件缺失或损坏，请重新解压或重新下载 PSX。({validationError})";
            StatusChanged?.Invoke(avx2Message);
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, avx2Message);
        }

        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            var noNodeMessage = "Portable npm is not available; cannot install the OpenCode baseline build.";
            StatusChanged?.Invoke(noNodeMessage);
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, noNodeMessage);
        }

        // The bundled binary needs AVX2, which this CPU lacks. Install the
        // baseline variant of the same version the release pinned. The
        // version read must NOT go through the AVX2-aware variant resolver:
        // on this machine the bundled modern package is "absent" for
        // execution purposes, but its manifest still carries the version the
        // baseline install must be pinned to.
        var pinnedVersion = ReadBundledModernVersion(paths)
            ?? MinimumCompatibleVersion;
        Log($"CPU lacks AVX2; installing {BaselinePackageName}@{pinnedVersion} into opencode-current.");

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke("正在安装 OpenCode（兼容版）");
            Directory.CreateDirectory(paths.RuntimeRoot);
            var install = await InstallVariantIntoDirectoryAsync(
                paths,
                paths.OpencodeCurrentDirectory,
                BaselinePackageName,
                pinnedVersion,
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(install.Message);
                return install;
            }

            var gateError = await VerifyInstalledDirectoryAsync(
                paths, paths.OpencodeCurrentDirectory, pinnedVersion, cancellationToken).ConfigureAwait(false);
            if (gateError != null)
            {
                StatusChanged?.Invoke(gateError);
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, gateError);
            }

            if (!TryWriteActivePointer(paths, ActiveCurrentToken))
            {
                var pointerMessage = "已安装 OpenCode（兼容版），但无法写入激活指针。";
                StatusChanged?.Invoke(pointerMessage);
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, pointerMessage);
            }

            Log($"OpenCode baseline {pinnedVersion} installed into opencode-current.");
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Success,
                $"OpenCode baseline {pinnedVersion} installed successfully.");
        }
        finally
        {
            _npmLock.Release();
        }
    }

    /// <summary>
    /// User-requested refresh. Runs a lightweight
    /// <c>npm view &lt;variant&gt;@latest version</c> pre-check first — the
    /// canonical version source is the platform package actually being
    /// installed (never the <c>opencode-ai</c> meta package, so a meta/platform
    /// publish skew cannot produce a blind install). Only a parseable, strictly
    /// newer registry version at or above
    /// <see cref="MinimumCompatibleVersion"/> is staged (pinned to that exact
    /// version) into <c>opencode-next</c>. The staged install must pass
    /// structure validation, an exact-version check and an exe
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
                "OpenCode runtime is not installed correctly; skipping refresh.");
        }

        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                "Portable npm is not available; cannot refresh OpenCode.");
        }

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke(BuildStatusText("正在检查更新"));

            Directory.CreateDirectory(paths.RuntimeRoot);
            var packageName = VariantPackageName;
            var view = await RunNpmAsync(
                paths,
                paths.RuntimeRoot,
                $"npm view {packageName}@latest version",
                new[] { "view", $"{packageName}@latest", "version", "--json" },
                cancellationToken).ConfigureAwait(false);
            if (view.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(view.Kind, view.Message, view.ExitCode);
            }

            var candidate = ParseNpmViewVersion(view.Stdout);
            var currentVersion = ReadOpencodeVersion(ResolveActiveOpencodeRoot(paths));
            if (!string.IsNullOrWhiteSpace(candidate)
                && !string.IsNullOrWhiteSpace(currentVersion)
                && string.Equals(candidate, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                ClearStaleNext(paths);
                TryWriteActivePointer(paths, ActiveCurrentToken);
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                    $"OpenCode {currentVersion} 已是最新版本。");
            }

            if (!Version.TryParse(candidate, out var parsedCandidate)
                || !Version.TryParse(currentVersion, out var parsedCurrent))
            {
                Log($"OpenCode version comparison is unsafe: registry='{candidate}', current='{currentVersion}'. Not updating.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "无法安全比较 Registry 版本，未执行更新。");
            }

            if (parsedCandidate < parsedCurrent)
            {
                Log($"Registry OpenCode {candidate} is older than current {currentVersion}; refusing to downgrade.");
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                    $"Registry 版本（{candidate}）低于当前版本（{currentVersion}），未执行更新。");
            }

            if (parsedCandidate < Version.Parse(MinimumCompatibleVersion))
            {
                Log($"Registry OpenCode {candidate} is below the minimum compatible {MinimumCompatibleVersion}; refusing to stage.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Registry 版本（{candidate}）低于最低兼容版本（{MinimumCompatibleVersion}），未执行更新。");
            }

            StatusChanged?.Invoke(BuildStatusText("正在更新 OpenCode"));
            var install = await InstallVariantIntoDirectoryAsync(
                paths,
                paths.OpencodeNextDirectory,
                packageName,
                candidate!,
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return install;
            }

            var gateError = await VerifyInstalledDirectoryAsync(
                paths, paths.OpencodeNextDirectory, candidate!, cancellationToken).ConfigureAwait(false);
            if (gateError != null)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, gateError);
            }

            if (!TryWriteActivePointer(paths, ActiveNextToken))
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "已下载更新，但无法写入激活指针，未切换版本。");
            }

            Log($"opencode-next staged at {candidate} and verified; active pointer flipped to 'next'. " +
                "Next PSX launch will use the new version.");
            StatusChanged?.Invoke(BuildStatusText($"已更新到 OpenCode {candidate}，下次启动生效"));
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Success,
                $"npm install {packageName}@{candidate} completed successfully.");
        }
        finally
        {
            _npmLock.Release();
        }
    }

    /// <summary>
    /// Builds a PowerShell invocation for the interactive OpenCode CLI using
    /// the resolved managed executable (not a PATH-dependent <c>opencode</c>).
    /// <paramref name="extraArgument"/> is appended verbatim (e.g.
    /// <c>auth login</c> or <c>--session '…'</c>). Returns null when the
    /// install is incomplete.
    /// </summary>
    public string? TryBuildInteractivePowerShellInvocation(string? extraArgument)
    {
        var paths = Paths;
        var validationError = ValidateActiveInstall(paths, out var exePath);
        if (validationError != null || exePath == null)
            return null;

        var command = new StringBuilder();
        command.Append("& ").Append(QuoteForPowerShell(exePath));
        if (!string.IsNullOrWhiteSpace(extraArgument))
            command.Append(' ').Append(extraArgument);
        return command.ToString();
    }

    // ---- internals ----

    private string ResolveActiveOpencodeRoot(RuntimePaths paths)
    {
        return ValidateOpencodeRoot(paths, paths.OpencodeCurrentDirectory, requireLockfile: false, out _) == null
            ? paths.OpencodeCurrentDirectory
            : paths.BundledOpencodeDirectory;
    }

    private string? ValidateActiveInstall(RuntimePaths paths, out string? exePath)
    {
        var activeRoot = ResolveActiveOpencodeRoot(paths);
        var isBundled = string.Equals(
            Path.GetFullPath(activeRoot),
            Path.GetFullPath(paths.BundledOpencodeDirectory),
            StringComparison.OrdinalIgnoreCase);
        return ValidateOpencodeRoot(paths, activeRoot, requireLockfile: isBundled, out exePath);
    }

    private string? ValidateOpencodeRoot(
        RuntimePaths paths,
        string opencodeRoot,
        bool requireLockfile,
        out string? exePath)
    {
        exePath = null;

        if (!Directory.Exists(opencodeRoot))
            return $"OpenCode directory is missing at {opencodeRoot}";

        if (requireLockfile && !File.Exists(Path.Combine(opencodeRoot, PackageLockName)))
            return $"{PackageLockName} is missing under {opencodeRoot}";

        var packageDir = FindVariantPackageDirectory(opencodeRoot);
        if (packageDir == null)
        {
            return SupportsAvx2
                ? $"neither {ModernPackageName} nor {BaselinePackageName} package.json " +
                  $"was found under {opencodeRoot}"
                : $"the OpenCode install under {opencodeRoot} is the modern build, which " +
                  $"requires an AVX2-capable CPU; the {BaselinePackageName} variant must be " +
                  "installed into runtime/opencode-current instead";
        }

        var resolvedExe = Path.GetFullPath(Path.Combine(packageDir, "bin", "opencode.exe"));
        var packageDirFull = Path.GetFullPath(packageDir);
        var packageDirPrefix = packageDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? packageDirFull
            : packageDirFull + Path.DirectorySeparatorChar;
        if (!resolvedExe.StartsWith(packageDirPrefix, StringComparison.OrdinalIgnoreCase))
            return $"resolved OpenCode executable escapes the package directory: {resolvedExe}";

        if (!File.Exists(resolvedExe))
            return $"resolved OpenCode executable is missing at {resolvedExe}";

        exePath = resolvedExe;
        return null;
    }

    /// <summary>
    /// Locates the installed platform package directory. On AVX2-capable
    /// machines either variant is accepted (modern preferred); on AVX2-less
    /// machines the baseline variant is preferred and a modern-only install
    /// resolves to null, so validation, snapshot, promote and process launch
    /// all refuse a binary this CPU cannot execute (an AVX2-only modern tree
    /// can land in <c>opencode-current</c> via a copied directory, a migrated
    /// disk, or a staged next promoted on different hardware).
    /// </summary>
    private string? FindVariantPackageDirectory(string opencodeRoot)
    {
        foreach (var name in SupportsAvx2 ? VariantPackageNames : BaselineFirstVariantPackageNames)
        {
            var packageDir = Path.Combine(opencodeRoot, "node_modules", name);
            if (!File.Exists(Path.Combine(packageDir, "package.json")))
                continue;

            // A modern build cannot execute on a CPU without AVX2, so a
            // modern-only install resolves to "absent": validation, snapshot,
            // promote and process launch all refuse it (a modern tree can
            // land in opencode-current via a copied directory, a migrated
            // disk, or a staged next promoted on different hardware).
            if (!SupportsAvx2
                && !string.Equals(name, BaselinePackageName, StringComparison.OrdinalIgnoreCase))
                return null;

            return packageDir;
        }

        return null;
    }

    /// <summary>
    /// Recreates <paramref name="targetDirectory"/> from scratch and installs
    /// exactly one platform package variant into it. The staging manifest is
    /// written (not copied from the bundled seed) so its dependency set always
    /// matches the selected variant — copying the bundled package.json on an
    /// AVX2-less machine would drag the modern package in alongside baseline.
    /// </summary>
    private async Task<AcpRuntimeOperationResult> InstallVariantIntoDirectoryAsync(
        RuntimePaths paths,
        string targetDirectory,
        string packageName,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, recursive: true);
            Directory.CreateDirectory(targetDirectory);
            WriteStagingManifest(targetDirectory);

            var bundledNpmrc = Path.Combine(paths.BundledOpencodeDirectory, ".npmrc");
            if (File.Exists(bundledNpmrc))
                File.Copy(bundledNpmrc, Path.Combine(targetDirectory, ".npmrc"), overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"Failed to prepare {targetDirectory}: {ex}");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                $"Failed to prepare the OpenCode staging directory: {ex.Message}");
        }

        var install = await RunNpmAsync(
            paths,
            targetDirectory,
            $"npm install {packageName}@{version}",
            new[]
            {
                "install",
                $"{packageName}@{version}",
                "--save-exact",
                "--omit=dev",
                "--no-audit",
                "--no-fund"
            },
            cancellationToken).ConfigureAwait(false);
        return new AcpRuntimeOperationResult(install.Kind, install.Message, install.ExitCode);
    }

    private static void WriteStagingManifest(string targetDirectory)
    {
        var manifest = new
        {
            @private = true,
            name = "psx-opencode-runtime",
            version = "0.1.0",
            description = "PSX-managed OpenCode install. dependencies are written by npm install --save-exact.",
            dependencies = new { }
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(targetDirectory, "package.json"), json + Environment.NewLine, Encoding.UTF8);
    }

    /// <summary>
    /// The post-install gates: structure validation, exact-version match and
    /// an exe <c>--version</c> smoke check. Returns null when all pass.
    /// </summary>
    private async Task<string?> VerifyInstalledDirectoryAsync(
        RuntimePaths paths,
        string directory,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var structureError = ValidateOpencodeRoot(paths, directory, requireLockfile: false, out var exePath);
        if (structureError != null || exePath == null)
        {
            Log($"Post-install validation failed for {directory}: {structureError}");
            return $"The installed OpenCode is incomplete; not switching to it. ({structureError})";
        }

        var stagedVersion = ReadOpencodeVersion(directory);
        if (!string.Equals(stagedVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Installed OpenCode version '{stagedVersion}' does not match requested '{expectedVersion}'.");
            return $"Installed OpenCode version ({stagedVersion}) does not match the requested {expectedVersion}; not switching to it.";
        }

        var smokeError = await RunStagedSmokeCheckAsync(exePath, cancellationToken).ConfigureAwait(false);
        if (smokeError != null)
        {
            Log($"OpenCode smoke check failed for {directory}: {smokeError}");
            return $"The installed OpenCode failed its start check; not switching to it. ({smokeError})";
        }

        return null;
    }

    private static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";

    private void PromoteNextToCurrent(RuntimePaths paths)
    {
        var backup = paths.OpencodeCurrentDirectory + ".old";
        try
        {
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);

            if (Directory.Exists(paths.OpencodeCurrentDirectory))
                Directory.Move(paths.OpencodeCurrentDirectory, backup);

            Directory.Move(paths.OpencodeNextDirectory, paths.OpencodeCurrentDirectory);
            Directory.Delete(backup, recursive: true);
        }
        catch (Exception ex)
        {
            Log($"OpenCode promote failed mid-swap: {ex}. Recovering.");
            if (Directory.Exists(backup) && !Directory.Exists(paths.OpencodeCurrentDirectory))
            {
                try { Directory.Move(backup, paths.OpencodeCurrentDirectory); } catch { /* give up */ }
            }
            TryWriteActivePointer(paths, ActiveCurrentToken);
            return;
        }

        TryWriteActivePointer(paths, ActiveCurrentToken);
        Log("OpenCode promote: opencode-next is now opencode-current.");
        StatusChanged?.Invoke(BuildStatusText());
    }

    /// <summary>
    /// Drops the self-updated copies when a PSX release ships an equal or
    /// newer bundled baseline. Only called on AVX2-capable machines — on
    /// AVX2-less machines the bundled binary cannot run and the runtime copy
    /// (baseline variant) must be kept.
    /// </summary>
    private void DropRuntimeCopyWhenBundledIsSameOrNewer(RuntimePaths paths)
    {
        if (ValidateOpencodeRoot(paths, paths.OpencodeCurrentDirectory, requireLockfile: false, out _) != null)
            return;
        if (ValidateOpencodeRoot(paths, paths.BundledOpencodeDirectory, requireLockfile: true, out _) != null)
            return;

        var bundledVersion = ReadOpencodeVersion(paths.BundledOpencodeDirectory);
        var currentVersion = ReadOpencodeVersion(paths.OpencodeCurrentDirectory);
        if (!IsSameOrNewer(bundledVersion, currentVersion))
            return;

        Log($"Bundled OpenCode {bundledVersion} supersedes self-updated {currentVersion}; dropping runtime copies.");
        foreach (var directory in new[] { paths.OpencodeCurrentDirectory, paths.OpencodeNextDirectory })
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

    private void ClearStaleNext(RuntimePaths paths)
    {
        try
        {
            if (Directory.Exists(paths.OpencodeNextDirectory))
                Directory.Delete(paths.OpencodeNextDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Log($"WARN: could not clear stale opencode-next: {ex.Message}");
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
            await RuntimeProcessCleanup.TerminateAndDrainAsync(
                process, stdoutTask, stderrTask, Log).ConfigureAwait(false);
            Log($"{label} timed out after {_processTimeout.TotalMinutes:0.##} minutes.");
            return new NpmRunOutcome(AcpRuntimeOperationKind.Failed,
                $"{label} timed out.", null, "");
        }
        catch (OperationCanceledException)
        {
            await RuntimeProcessCleanup.TerminateAndDrainAsync(
                process, stdoutTask, stderrTask, Log).ConfigureAwait(false);
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

    /// <summary>
    /// Smoke check for a staged binary: run <c>opencode.exe --version</c>
    /// directly (native executable — no Node host) and require exit code 0
    /// with non-empty output.
    /// </summary>
    private async Task<string?> RunStagedSmokeCheckAsync(
        string exePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--version");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return $"failed to start the staged executable: {ex.Message}";
        }
        if (process == null)
            return "the staged executable process did not start";
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
        if (stdout.Length == 0)
            return "--version produced no output";

        Log($"Staged smoke check passed: --version -> {stdout}");
        return null;
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
            if (!File.Exists(paths.OpencodeActivePointerFile))
                return false;
            var token = File.ReadAllText(paths.OpencodeActivePointerFile).Trim();
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
            var tmp = paths.OpencodeActivePointerFile + ".tmp";
            File.WriteAllText(tmp, token);
            File.Move(tmp, paths.OpencodeActivePointerFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to write OpenCode active pointer '{token}': {ex}");
            return false;
        }
    }

    private string? ReadOpencodeVersion(string opencodeRoot)
    {
        try
        {
            var packageDir = FindVariantPackageDirectory(opencodeRoot);
            if (packageDir == null)
                return null;

            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(packageDir, "package.json")));
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
    /// Reads the bundled modern package's manifest version with no AVX2 /
    /// executability judgment. Used to pin the baseline install on AVX2-less
    /// machines to the exact version the release shipped — reading through
    /// the AVX2-aware resolver would report "absent" there and silently fall
    /// back to <see cref="MinimumCompatibleVersion"/>.
    /// </summary>
    private static string? ReadBundledModernVersion(RuntimePaths paths)
    {
        try
        {
            var packageJsonPath = Path.Combine(
                paths.BundledOpencodeDirectory, "node_modules", ModernPackageName, "package.json");
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
            // Best effort; the caller falls back to MinimumCompatibleVersion.
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
