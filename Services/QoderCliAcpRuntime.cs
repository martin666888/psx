using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Managed Qoder CLI runtime. The public release ships only
/// <c>tools/qoder-seed/</c> (manifest + lockfile + platform policy). After
/// explicit user confirmation, PSX installs the pinned
/// <c>@qoder-ai/qodercli</c> into <c>runtime/qoder-current/</c> via portable
/// Node with <c>--ignore-scripts</c> (the package postinstall is advisory and
/// may mutate the user PATH on Windows). Updates stage into
/// <c>runtime/qoder-next/</c>, flip <c>qoder-active.txt</c>, and promote on the
/// next PSX launch — never mid-session.
///
/// Process form: <c>node &lt;bundle/qodercli.js&gt; --acp</c>. The entry is
/// resolved from the package <c>bin.qodercli</c> field with a path-escape guard.
/// </summary>
public sealed class QoderCliAcpRuntime : IAcpAgentRuntime
{
    public const string PackageName = "@qoder-ai/qodercli";
    public const string SeededPackageVersion = "1.1.14";
    public static readonly Version MinimumCompatibleVersion = new(1, 1, 14);

    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";
    private const string PackageLockName = "package-lock.json";

    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SmokeCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string QoderPackageDirSubpath =
        Path.Combine("node_modules", "@qoder-ai", "qodercli");
    private static readonly string QoderPackageJsonSubpath =
        Path.Combine(QoderPackageDirSubpath, "package.json");

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;
    private readonly TimeSpan _processTimeout;
    private readonly NpmRuntimeProcessRunner _npmRunner;
    private readonly StagedRuntimeStore _stagedStore;
    private readonly SemaphoreSlim _npmLock = new(1, 1);

    public event Action<string>? StatusChanged;

    public QoderCliAcpRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, DefaultProcessTimeout)
    {
    }

    internal QoderCliAcpRuntime(RuntimeLocator locator, string logDirectory, TimeSpan processTimeout)
    {
        if (processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processTimeout = processTimeout;
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "qoder-runtime.log");
        _npmRunner = new NpmRuntimeProcessRunner(_processTimeout, Log);
        _stagedStore = new StagedRuntimeStore("Qoder", Log);
    }

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    public bool SupportsSelfUpdate => true;

    public bool IsReady()
    {
        var paths = Paths;
        return ValidateQoderRoot(paths, paths.QoderCurrentDirectory, out _) == null;
    }

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        var paths = Paths;
        var currentVersion = IsReady() ? ReadQoderVersion(paths.QoderCurrentDirectory) : null;
        string? pendingVersion = null;
        if (PointerSaysNext(paths)
            && ValidateQoderRoot(paths, paths.QoderNextDirectory, out _) == null)
        {
            pendingVersion = ReadQoderVersion(paths.QoderNextDirectory);
        }

        var hasPending = pendingVersion != null
            && !string.Equals(pendingVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        return new RuntimeVersionSnapshot(
            CurrentVersion: currentVersion,
            PendingVersion: hasPending ? pendingVersion : null,
            HasPendingUpdate: hasPending)
        {
            ProductName = "Qoder CLI"
        };
    }

    public string BuildStatusText(string? suffix = null)
    {
        var version = GetVersionSnapshot().CurrentVersion;
        var baseText = string.IsNullOrWhiteSpace(version)
            ? (IsReady() ? "Qoder CLI" : "Qoder CLI 未安装 · 首次使用时安装")
            : $"Qoder CLI {version}";
        return string.IsNullOrWhiteSpace(suffix) ? baseText : $"{baseText} · {suffix}";
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;
            if (PointerSaysNext(paths))
            {
                if (ValidateQoderRoot(paths, paths.QoderNextDirectory, out _) != null)
                {
                    Log("Qoder promote aborted: qoder-next is incomplete; reverting pointer to 'current'.");
                    TryWriteActivePointer(paths, ActiveCurrentToken);
                }
                else
                {
                    PromoteNextToCurrent(paths);
                }
            }
            else if (File.Exists(paths.QoderActivePointerFile))
            {
                TryWriteActivePointer(paths, ActiveCurrentToken);
            }
        }
        finally
        {
            _npmLock.Release();
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
                "Qoder CLI runtime is already installed.");
        }

        var writeFailure = await CheckRuntimeDirectoryWritableAsync(cancellationToken).ConfigureAwait(false);
        if (writeFailure != null)
            return writeFailure;

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureInstalledCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _npmLock.Release();
        }
    }

    public async Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Paths;
        if (ValidateQoderRoot(paths, paths.QoderCurrentDirectory, out _) != null)
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Qoder CLI runtime is not installed; skipping refresh.");
        }

        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                "Portable npm is not available; cannot refresh Qoder CLI.");
        }

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke(BuildStatusText("正在检查更新"));

            Directory.CreateDirectory(paths.RuntimeRoot);
            var view = await RunNpmAsync(
                paths,
                paths.RuntimeRoot,
                $"npm view {PackageName}@latest version",
                new[] { "view", $"{PackageName}@latest", "version", "--json" },
                cancellationToken).ConfigureAwait(false);
            if (view.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(view.Kind, view.Message, view.ExitCode);
            }

            var candidate = ParseNpmViewVersion(view.Stdout);
            var currentVersion = ReadQoderVersion(paths.QoderCurrentDirectory);
            if (!string.IsNullOrWhiteSpace(candidate)
                && !string.IsNullOrWhiteSpace(currentVersion)
                && string.Equals(candidate, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                ClearStaleNext(paths);
                TryWriteActivePointer(paths, ActiveCurrentToken);
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.AlreadyReady,
                    $"Qoder CLI {currentVersion} 已是最新版本。");
            }

            if (!Version.TryParse(candidate, out var parsedCandidate)
                || !Version.TryParse(currentVersion, out var parsedCurrent))
            {
                Log($"Qoder version comparison is unsafe: registry='{candidate}', current='{currentVersion}'. Not updating.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "无法安全比较 Registry 版本，未执行更新。");
            }

            if (parsedCandidate < parsedCurrent)
            {
                Log($"Registry Qoder {candidate} is older than current {currentVersion}; refusing to downgrade.");
                StatusChanged?.Invoke(BuildStatusText("已是最新版本"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.AlreadyReady,
                    $"Registry 版本（{candidate}）低于当前版本（{currentVersion}），未执行更新。");
            }

            if (parsedCandidate < MinimumCompatibleVersion)
            {
                Log($"Registry Qoder {candidate} is below MinimumCompatibleVersion {MinimumCompatibleVersion}.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Registry 版本（{candidate}）低于 PSX 所需的最低版本 {MinimumCompatibleVersion}，未执行更新。");
            }

            try
            {
                if (Directory.Exists(paths.QoderNextDirectory))
                    Directory.Delete(paths.QoderNextDirectory, recursive: true);
                Directory.CreateDirectory(paths.QoderNextDirectory);
                SeedManifestFiles(paths, paths.QoderNextDirectory, includeLockfile: false);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare qoder-next: {ex}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Failed to prepare qoder-next: {ex.Message}");
            }

            var installLabel = $"npm install {PackageName}@{candidate}";
            StatusChanged?.Invoke(BuildStatusText("正在更新 Qoder CLI"));
            var install = await RunNpmAsync(
                paths,
                paths.QoderNextDirectory,
                installLabel,
                new[]
                {
                    "install",
                    $"{PackageName}@{candidate}",
                    "--save-exact",
                    "--omit=dev",
                    "--ignore-scripts",
                    "--no-audit",
                    "--no-fund"
                },
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(install.Kind, install.Message, install.ExitCode);
            }

            var stagedError = ValidateQoderRoot(paths, paths.QoderNextDirectory, out var stagedEntry);
            if (stagedError != null || stagedEntry == null)
            {
                Log($"Post-update validation failed: {stagedError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Updated Qoder CLI is incomplete; not switching to it. ({stagedError})");
            }

            var stagedVersion = ReadQoderVersion(paths.QoderNextDirectory);
            if (!string.Equals(stagedVersion, candidate, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Staged Qoder version '{stagedVersion}' does not match requested '{candidate}'.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Staged Qoder CLI version ({stagedVersion}) does not match the requested {candidate}; not switching to it.");
            }

            var smokeError = await RunStagedSmokeCheckAsync(paths, stagedEntry, cancellationToken)
                .ConfigureAwait(false);
            if (smokeError != null)
            {
                Log($"Staged Qoder smoke check failed: {smokeError}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    $"Staged Qoder CLI failed its start check; not switching to it. ({smokeError})");
            }

            if (!TryWriteActivePointer(paths, ActiveNextToken))
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用"));
                return new AcpRuntimeOperationResult(
                    AcpRuntimeOperationKind.Failed,
                    "已下载更新，但无法写入激活指针，未切换版本。");
            }

            Log($"qoder-next staged at {candidate} and verified; active pointer flipped to 'next'.");
            StatusChanged?.Invoke(BuildStatusText($"已更新到 Qoder CLI {candidate}，下次启动生效"));
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Success,
                $"{installLabel} completed successfully.");
        }
        finally
        {
            _npmLock.Release();
        }
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        var paths = Paths;
        var validationError = ValidateQoderRoot(paths, paths.QoderCurrentDirectory, out var entryPath);
        if (validationError != null || entryPath == null)
        {
            throw new InvalidOperationException(
                validationError
                ?? $"Qoder CLI runtime is not installed under {paths.QoderCurrentDirectory}. " +
                   "Confirm installation from Agent mode first.");
        }

        var nodePath = paths.PortableNodePath;
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new InvalidOperationException(
                "Portable Node.js was not found next to PSX.exe. The release zip should include tools/node/node.exe.");
        }

        var environment = new Dictionary<string, string?>();
        ForwardEnvironmentVariable(environment, "PATH");
        ForwardEnvironmentVariable(environment, "QODER_PERSONAL_ACCESS_TOKEN");

        return new AcpProcessSpec
        {
            FileName = nodePath,
            WorkingDirectory = workingDirectory,
            Arguments = new[] { entryPath, "--acp" },
            Environment = environment
        };
    }

    /// <summary>
    /// Builds a PowerShell invocation for the interactive Qoder CLI using the
    /// resolved portable Node + package entry (not a PATH-dependent shim).
    /// Pass <paramref name="extraArgument"/> as <c>login</c> for the login
    /// profile. Returns null when the install is incomplete.
    /// </summary>
    public string? TryBuildInteractivePowerShellInvocation(string? extraArgument = null)
    {
        var paths = Paths;
        var validationError = ValidateQoderRoot(paths, paths.QoderCurrentDirectory, out var entryPath);
        if (validationError != null || entryPath == null || string.IsNullOrWhiteSpace(paths.PortableNodePath))
            return null;

        var command = new StringBuilder();
        command.Append("& ").Append(QuoteForPowerShell(paths.PortableNodePath));
        command.Append(' ').Append(QuoteForPowerShell(entryPath));
        if (!string.IsNullOrWhiteSpace(extraArgument))
            command.Append(' ').Append(QuoteForPowerShell(extraArgument));
        return command.ToString();
    }

    public void Dispose()
    {
        _npmLock.Dispose();
    }

    // ---- internals ----

    private async Task<AcpRuntimeOperationResult> EnsureInstalledCoreAsync(
        CancellationToken cancellationToken)
    {
        if (IsReady())
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Qoder CLI runtime is already installed.");
        }

        StatusChanged?.Invoke("正在安装 Qoder CLI");

        var paths = Paths;
        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
        {
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                "No node runtime available; cannot install Qoder CLI.");
        }

        if (!Directory.Exists(paths.QoderSeedDirectory)
            || !File.Exists(Path.Combine(paths.QoderSeedDirectory, "package.json")))
        {
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                $"Qoder seed is missing at {paths.QoderSeedDirectory}.");
        }

        var pinnedVersion = ReadSeededPackageVersion(paths) ?? SeededPackageVersion;

        try
        {
            Directory.CreateDirectory(paths.RuntimeRoot);
            if (Directory.Exists(paths.QoderCurrentDirectory))
                Directory.Delete(paths.QoderCurrentDirectory, recursive: true);
            Directory.CreateDirectory(paths.QoderCurrentDirectory);
            if (Directory.Exists(paths.QoderNextDirectory))
            {
                try { Directory.Delete(paths.QoderNextDirectory, recursive: true); }
                catch (Exception ex) { Log($"WARN: could not clear stale qoder-next: {ex.Message}"); }
            }

            SeedManifestFiles(paths, paths.QoderCurrentDirectory, includeLockfile: true);
        }
        catch (Exception ex)
        {
            Log($"Failed to seed qoder-current: {ex}");
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                $"Failed to seed qoder-current: {ex.Message}");
        }

        var installLabel = $"npm install {PackageName}@{pinnedVersion}";
        var install = await RunNpmAsync(
            paths,
            paths.QoderCurrentDirectory,
            installLabel,
            new[]
            {
                "install",
                $"{PackageName}@{pinnedVersion}",
                "--save-exact",
                "--omit=dev",
                "--ignore-scripts",
                "--no-audit",
                "--no-fund"
            },
            cancellationToken).ConfigureAwait(false);
        if (install.Kind != AcpRuntimeOperationKind.Success)
        {
            if (install.Kind == AcpRuntimeOperationKind.Cancelled)
                StatusChanged?.Invoke("Qoder CLI 安装已取消");
            else
                StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(install.Kind, install.Message, install.ExitCode);
        }

        var validationError = ValidateQoderRoot(paths, paths.QoderCurrentDirectory, out var entryPath);
        if (validationError != null || entryPath == null)
        {
            Log($"First-install validation failed: {validationError}");
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                $"Installed Qoder CLI is incomplete; not activating it. ({validationError})");
        }

        var installedVersion = ReadQoderVersion(paths.QoderCurrentDirectory);
        if (!string.Equals(installedVersion, pinnedVersion, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Installed Qoder version '{installedVersion}' does not match pinned '{pinnedVersion}'.");
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                $"Installed Qoder CLI version ({installedVersion}) does not match the pinned {pinnedVersion}; not activating it.");
        }

        var smokeError = await RunStagedSmokeCheckAsync(paths, entryPath, cancellationToken).ConfigureAwait(false);
        if (smokeError != null)
        {
            Log($"First-install smoke check failed: {smokeError}");
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                $"Installed Qoder CLI failed its start check; not activating it. ({smokeError})");
        }

        if (!TryWriteActivePointer(paths, ActiveCurrentToken))
        {
            StatusChanged?.Invoke("Qoder CLI 安装失败，Agent 暂不可用");
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                "Installed Qoder CLI, but failed to write the active pointer.");
        }

        StatusChanged?.Invoke(BuildStatusText());
        return new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Success,
            $"{installLabel} completed successfully.");
    }

    private async Task<AcpRuntimeOperationResult?> CheckRuntimeDirectoryWritableAsync(
        CancellationToken cancellationToken)
    {
        var paths = Paths;
        string? probePath = null;
        try
        {
            Directory.CreateDirectory(paths.RuntimeRoot);
            probePath = Path.Combine(paths.RuntimeRoot, $".psx-write-probe-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probePath, "PSX runtime write probe", cancellationToken)
                .ConfigureAwait(false);
            File.Delete(probePath);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (probePath != null)
            {
                try { File.Delete(probePath); } catch { }
            }

            var message =
                $"PSX cannot write to its runtime folder. Extract PSX to a writable folder and retry. {ex.Message}";
            StatusChanged?.Invoke(message);
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, message);
        }
    }

    private static string? ValidateQoderRoot(
        RuntimePaths paths,
        string qoderRoot,
        out string? entryPath)
    {
        entryPath = null;

        if (string.IsNullOrWhiteSpace(paths.PortableNodePath) || !File.Exists(paths.PortableNodePath))
            return "portable Node.js (tools/node/node.exe) is missing";

        if (!Directory.Exists(qoderRoot))
            return $"Qoder directory is missing at {qoderRoot}";

        var packageDir = Path.Combine(qoderRoot, QoderPackageDirSubpath);
        var packageJsonPath = Path.Combine(qoderRoot, QoderPackageJsonSubpath);
        if (!File.Exists(packageJsonPath))
            return $"{PackageName} package.json is missing under {qoderRoot}";

        string? binRelative;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            binRelative = ExtractBinPath(document.RootElement);
        }
        catch (Exception ex)
        {
            return $"failed to parse {PackageName} package.json: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(binRelative))
            return $"{PackageName} package.json does not declare a 'bin.qodercli' entry";

        var packageDirFull = Path.GetFullPath(packageDir);
        var resolvedEntry = Path.GetFullPath(Path.Combine(packageDirFull, binRelative));
        var packageDirPrefix = packageDirFull.EndsWith(Path.DirectorySeparatorChar)
            ? packageDirFull
            : packageDirFull + Path.DirectorySeparatorChar;
        if (!resolvedEntry.StartsWith(packageDirPrefix, StringComparison.OrdinalIgnoreCase))
            return $"'bin.qodercli' path '{binRelative}' escapes the Qoder package directory";

        if (!File.Exists(resolvedEntry))
            return $"resolved Qoder entry point is missing at {resolvedEntry}";

        entryPath = resolvedEntry;
        return null;
    }

    private static string? ExtractBinPath(JsonElement root)
    {
        if (!root.TryGetProperty("bin", out var bin))
            return null;

        if (bin.ValueKind == JsonValueKind.String)
            return bin.GetString();

        if (bin.ValueKind == JsonValueKind.Object)
        {
            if (bin.TryGetProperty("qodercli", out var qoderBin) && qoderBin.ValueKind == JsonValueKind.String)
                return qoderBin.GetString();

            foreach (var property in bin.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ReadQoderVersion(string qoderRoot)
    {
        try
        {
            var packageJsonPath = Path.Combine(qoderRoot, QoderPackageJsonSubpath);
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

    private static string? ReadSeededPackageVersion(RuntimePaths paths)
    {
        try
        {
            var packageJsonPath = Path.Combine(paths.QoderSeedDirectory, "package.json");
            if (!File.Exists(packageJsonPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.TryGetProperty("dependencies", out var deps)
                && deps.ValueKind == JsonValueKind.Object
                && deps.TryGetProperty(PackageName, out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                return version.GetString()?.Trim().TrimStart('^', '~', '=', 'v', 'V');
            }
        }
        catch
        {
            // Fall back to the compile-time pin.
        }

        return null;
    }

    private void SeedManifestFiles(RuntimePaths paths, string destination, bool includeLockfile)
    {
        var names = includeLockfile
            ? new[] { "package.json", PackageLockName, ".npmrc" }
            : new[] { "package.json", ".npmrc" };
        foreach (var name in names)
        {
            var source = Path.Combine(paths.QoderSeedDirectory, name);
            if (!File.Exists(source))
                continue;
            var dest = Path.Combine(destination, name);
            File.Copy(source, dest, overwrite: true);
            Log($"Seeded {name} -> {dest}");
        }
    }

    private void PromoteNextToCurrent(RuntimePaths paths)
    {
        if (_stagedStore.PromoteNextToCurrent(
                paths.RuntimeRoot,
                paths.QoderCurrentDirectory,
                paths.QoderNextDirectory,
                paths.QoderActivePointerFile,
                ActiveCurrentToken))
        {
            StatusChanged?.Invoke(BuildStatusText());
        }
    }
    private void ClearStaleNext(RuntimePaths paths)
        => _stagedStore.ClearStaleNext(paths.QoderNextDirectory);
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
        => _stagedStore.PointerSaysNext(paths.QoderActivePointerFile, ActiveNextToken);
    private bool TryWriteActivePointer(RuntimePaths paths, string token)
        => _stagedStore.TryWriteActivePointer(paths.RuntimeRoot, paths.QoderActivePointerFile, token);
    private static void ForwardEnvironmentVariable(IDictionary<string, string?> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            environment[name] = value;
    }

    private static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";

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
