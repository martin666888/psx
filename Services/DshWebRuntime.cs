using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// DeepSeek Harness runtime installation: seed → scratch → validate → atomic
/// swap onto <c>runtime/dsh-current</c>. NOT an IAcpAgentRuntime — DSH has no
/// ACP surface. npm install runs WITH lifecycle scripts.
/// </summary>
public sealed class DshWebRuntime
{
    public const string DshPackageName = "@deepseek-ai/dsh";
    public const string SeededPackageVersion = "0.1.0-rc.6";

    private const string OfficialRegistry = "https://registry.npmjs.org/";

    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";
    private static readonly string DshEntryPath =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    private static readonly string DshPackageJson =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "package.json");
    private const string UpdateReceiptName = "psx-dsh-update.json";
    private static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan[] FileSystemRetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200)
    ];

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;
    private readonly NpmRuntimeProcessRunner _npmRunner;
    private readonly StagedRuntimeStore _stagedStore;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public DshWebRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, DefaultInstallTimeout) { }

    internal DshWebRuntime(RuntimeLocator locator, string logDirectory, TimeSpan installTimeout)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "dsh-runtime.log");
        _npmRunner = new NpmRuntimeProcessRunner(installTimeout, Log);
        _stagedStore = new StagedRuntimeStore("DSH", Log);
    }

    public string LogPath => _logPath;
    public RuntimePaths Paths => _locator.Locate();

    /// <summary>
    /// A runtime is launchable only when both files exist and the installed
    /// package carries a valid semantic version at or above the release seed.
    /// First install remains pinned to <see cref="SeededPackageVersion"/>;
    /// user-confirmed updates may advance beyond it without weakening the
    /// launch gate into a mere file-exists check.
    /// </summary>
    public bool IsInstalled()
    {
        var paths = Paths;
        return IsLaunchableTree(paths.DshCurrentDirectory);
    }

    internal static bool IsLaunchableTree(string directory) =>
        File.Exists(Path.Combine(directory, DshPackageJson))
        && File.Exists(Path.Combine(directory, DshEntryPath))
        && IsAuthorizedVersion(directory);

    private static bool IsAuthorizedVersion(string directory)
    {
        var version = ReadPackageVersion(Path.Combine(directory, DshPackageJson));
        if (!IsCompatibleVersion(version))
            return false;
        if (string.Equals(version, SeededPackageVersion, StringComparison.Ordinal))
            return true;
        return string.Equals(ReadUpdateReceiptVersion(directory), version, StringComparison.Ordinal)
            && ValidateStagedUpdate(directory, version!) == null;
    }

    private static bool IsCompatibleVersion(string? version) =>
        DshSemanticVersion.TryParse(version, out var parsed)
        && DshSemanticVersion.TryParse(SeededPackageVersion, out var seeded)
        && parsed.CompareTo(seeded) >= 0;

    public string? CurrentVersion
    {
        get
        {
            var directory = Paths.DshCurrentDirectory;
            return IsLaunchableTree(directory)
                ? ReadPackageVersion(Path.Combine(directory, DshPackageJson))
                : null;
        }
    }

    public DshLaunchSpec? CreateLaunchSpec()
    {
        var paths = Paths;
        if (paths.PortableNodePath == null || !IsInstalled())
            return null;
        return new DshLaunchSpec(
            paths.PortableNodePath,
            Path.Combine(paths.DshCurrentDirectory, DshEntryPath));
    }

    public async Task<DshInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await InstallCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _installLock.Release(); }
    }

    /// <summary>
    /// Query the official npm registry for published versions and dist-tags.
    /// This is a metadata-only operation: it never changes the active or staged tree.
    /// Dist-tags are annotations only; the candidate set is every published
    /// version that is &gt;= the seed and strictly newer than the installed tree.
    /// </summary>
    public async Task<DshUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;
            var current = CurrentVersion;
            if (!IsInstalled() || !DshSemanticVersion.TryParse(current, out _))
                return DshUpdateCheckResult.Failed(current, "请先安装 DeepSeek Harness。");
            if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
                return DshUpdateCheckResult.Failed(current, "缺少便携 Node.js，无法检查更新。");

            Directory.CreateDirectory(paths.RuntimeRoot);
            var view = await _npmRunner.RunAsync(
                paths.PortableNodePath,
                paths.PortableNpmCliPath,
                paths.RuntimeRoot,
                "DSH update check",
                new[]
                {
                    "view", DshPackageName, "versions", "dist-tags", "--json",
                    $"--registry={OfficialRegistry}", "--prefer-online"
                },
                cancellationToken).ConfigureAwait(false);
            if (view.Kind != AcpRuntimeOperationKind.Success)
            {
                Log($"DSH update check failed: {view.Kind} exit={view.ExitCode} detail={view.Message}");
                var error = view.Kind == AcpRuntimeOperationKind.NetworkUnavailable
                    ? "无法连接 npm 仓库，请检查网络后重试。"
                    : "检查更新失败，当前版本可继续使用。";
                return DshUpdateCheckResult.Failed(current, error);
            }

            if (!TryBuildUpdateCatalog(view.Stdout, current, out var catalog, out var parseError))
            {
                Log($"DSH registry catalog parse failed: {parseError}");
                return DshUpdateCheckResult.Failed(current, "npm 仓库返回了无效版本，未执行更新。");
            }

            if (catalog.Count == 0)
                return DshUpdateCheckResult.UpToDate(current);
            return DshUpdateCheckResult.Available(current, catalog);
        }
        finally { _installLock.Release(); }
    }

    /// <summary>
    /// Install an exact, previously checked version into dsh-next. The live
    /// server keeps reading dsh-current until the supervisor explicitly
    /// applies the staged tree after user confirmation.
    /// </summary>
    public async Task<DshUpdateResult> StageUpdateAsync(
        string candidate,
        CancellationToken cancellationToken,
        Action? validationStarted = null)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;
            var current = CurrentVersion;
            if (!IsInstalled()
                || !DshSemanticVersion.TryParse(current, out var currentVersion)
                || !DshSemanticVersion.TryParse(candidate, out var candidateVersion)
                || candidateVersion.CompareTo(currentVersion) <= 0)
            {
                return new(false, current, candidate, "待更新版本无效或不高于当前版本。", null);
            }
            if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
                return new(false, current, candidate, "缺少便携 Node.js，无法下载更新。", null);

            try
            {
                DeleteDirectory(paths.DshNextDirectory);
                Directory.CreateDirectory(paths.DshNextDirectory);
                var seedNpmrc = Path.Combine(paths.DshSeedDirectory, ".npmrc");
                if (!File.Exists(seedNpmrc))
                    return new(false, current, candidate, "缺少 DSH Registry 配置，无法安全更新。", null);
                File.Copy(seedNpmrc, Path.Combine(paths.DshNextDirectory, ".npmrc"), overwrite: true);
                WriteUpdatePackageJson(paths.DshNextDirectory, candidate);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare dsh-next: {ex}");
                return new(false, current, candidate, "无法准备 DSH 更新目录。", null);
            }

            var install = await _npmRunner.RunAsync(
                paths.PortableNodePath,
                paths.PortableNpmCliPath,
                paths.DshNextDirectory,
                $"DSH update {candidate}",
                new[]
                {
                    "install", $"{DshPackageName}@{candidate}", "--save-exact",
                    "--omit=dev", "--include=optional", "--engine-strict",
                    "--no-audit", "--no-fund", $"--registry={OfficialRegistry}"
                },
                cancellationToken).ConfigureAwait(false);
            if (install.Kind != AcpRuntimeOperationKind.Success)
            {
                Log($"DSH update npm result: {install.Kind} exit={install.ExitCode} detail={install.Message}");
                var error = install.Kind switch
                {
                    AcpRuntimeOperationKind.NetworkUnavailable => "更新下载失败，请检查网络后重试。",
                    AcpRuntimeOperationKind.Cancelled => "更新已停止，当前版本未改变。",
                    _ => "更新安装失败，当前版本可继续使用。"
                };
                return new(false, current, candidate, error, install.ExitCode);
            }

            validationStarted?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            try { WriteUpdateReceipt(paths.DshNextDirectory, candidate); }
            catch (Exception ex)
            {
                Log($"Failed to write DSH update receipt: {ex}");
                return new(false, current, candidate, "无法记录更新来源，当前版本未改变。", null);
            }
            var validationError = ValidateStagedUpdate(paths.DshNextDirectory, candidate);
            if (validationError != null)
            {
                Log($"DSH staged update validation failed: {validationError}");
                return new(false, current, candidate, "下载的更新未通过完整性校验，当前版本未改变。", null);
            }
            if (!_stagedStore.TryWriteActivePointer(
                    paths.RuntimeRoot, paths.DshActivePointerFile, ActiveNextToken))
                return new(false, current, candidate, "更新已下载，但无法写入激活标记。", null);

            Log($"DSH {candidate} staged in dsh-next and validated.");
            return new(true, current, candidate, null, null);
        }
        finally { _installLock.Release(); }
    }

    private async Task<DshInstallResult> InstallCoreAsync(CancellationToken cancellationToken)
    {
        var paths = Paths;
        if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null)
            return new(false, "缺少便携 Node.js 运行时，无法安装。", null);
        if (!Directory.Exists(paths.DshSeedDirectory))
            return new(false, "缺少 DSH seed 目录 (tools/dsh-seed)。", null);

        Log("Starting DSH install (lifecycle scripts enabled).");
        var scratch = paths.DshInstallingDirectory;
        try { DeleteDirectory(scratch); } catch { }
        var missingSeedFile = CopySeedManifests(paths.DshSeedDirectory, scratch);
        if (missingSeedFile != null)
        {
            Log($"DSH seed is incomplete: {missingSeedFile} is missing from {paths.DshSeedDirectory}.");
            return new(false, "缺少 DSH 安装种子文件，无法安装。", null);
        }

        var result = await _npmRunner.RunAsync(
            paths.PortableNodePath, paths.PortableNpmCliPath, scratch,
            "DSH install", new[] { "ci" }, cancellationToken).ConfigureAwait(false);

        if (result.Kind != AcpRuntimeOperationKind.Success)
        {
            // The npm runner's message is diagnostic and may carry paths; it
            // stays in the log. The wire error is a fixed safe string.
            Log($"DSH install npm result: {result.Kind} exit={result.ExitCode} detail={result.Message}");
            var message = result.Kind switch
            {
                AcpRuntimeOperationKind.NetworkUnavailable => "网络不可用，无法连接 npm 仓库。请检查网络后重试。",
                AcpRuntimeOperationKind.Cancelled => "安装已停止。",
                _ => "安装失败：npm ci 没有成功完成。"
            };
            return new(false, message, result.ExitCode);
        }
        return ValidateInstalledTree(paths, scratch);
    }

    /// <summary>
    /// Post-install gate: the tree must carry the entry file and exactly the
    /// seeded version before it may swap onto <c>dsh-current</c>. Any drift
    /// is refused; details stay in the log and the wire gets a fixed string.
    /// </summary>
    internal DshInstallResult ValidateInstalledTree(RuntimePaths paths, string scratch)
    {
        if (!File.Exists(Path.Combine(scratch, DshEntryPath)))
            return new(false, "安装后入口文件缺失 (lib/bin.js)。", null);
        var version = ReadPackageVersion(Path.Combine(scratch, DshPackageJson));
        if (string.IsNullOrWhiteSpace(version))
            return new(false, "安装后无法读取 DSH 版本。", null);
        if (!string.Equals(version, SeededPackageVersion, StringComparison.Ordinal))
        {
            Log($"DSH install produced version {version}; the seed pins {SeededPackageVersion}. Refusing the swap.");
            return new(false, "安装结果与锁定版本不一致，已拒绝启用。", null);
        }

        if (!TrySwapToCurrent(paths, scratch))
            return new(false, "安装目录切换失败，运行时未启用。", null);
        if (!IsInstalled())
            return new(false, "安装后运行时不可用。", null);

        Log("DSH install complete.");
        return new(true, null, null);
    }

    public void PrepareForStartup()
    {
        var paths = Paths;

        // A rollback directory exists only while a user-requested update is
        // awaiting a successful Ready signal. If PSX exited before that
        // commit, prefer the last known-good tree on the next launch.
        RecoverUncommittedUpdate(paths);

        if (_stagedStore.PointerSaysNext(paths.DshActivePointerFile, ActiveNextToken))
        {
            var currentVersion = CurrentVersion;
            var nextVersion = ReadPackageVersion(Path.Combine(paths.DshNextDirectory, DshPackageJson));
            // Promote only a complete, strictly newer staged candidate. The
            // rollback is kept until the new process reaches Ready.
            if (IsLaunchableTree(paths.DshNextDirectory)
                && IsStrictlyNewer(nextVersion, currentVersion)
                && TrySwapToCurrent(paths, paths.DshNextDirectory, preserveRollback: true))
            {
                Log($"DSH staged update {nextVersion} moved to current; awaiting Ready before commit.");
            }
            else
            {
                Log("DSH dsh-next candidate is incomplete, not newer, or could not be activated; discarding it.");
                _stagedStore.ClearStaleNext(paths.DshNextDirectory);
                _stagedStore.TryWriteActivePointer(
                    paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken);
            }
        }
        _stagedStore.ClearStaleNext(paths.DshNextDirectory);
    }

    public bool HasUncommittedUpdate => Directory.Exists(Paths.DshRollbackDirectory);

    public void DiscardStagedUpdate()
    {
        var paths = Paths;
        _stagedStore.ClearStaleNext(paths.DshNextDirectory);
        _stagedStore.TryWriteActivePointer(
            paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken);
    }

    /// <summary>Move a validated dsh-next onto current while retaining the old
    /// tree as rollback until the supervisor observes Ready.</summary>
    public bool ApplyStagedUpdate(string expectedVersion)
    {
        var paths = Paths;
        if (ValidateStagedUpdate(paths.DshNextDirectory, expectedVersion) != null)
            return false;
        return TrySwapToCurrent(paths, paths.DshNextDirectory, preserveRollback: true)
            && string.Equals(CurrentVersion, expectedVersion, StringComparison.Ordinal);
    }

    /// <summary>The replacement reached Ready; the last-known-good rollback
    /// is no longer required.</summary>
    public void CommitAppliedUpdate()
    {
        var paths = Paths;
        try { DeleteDirectory(paths.DshRollbackDirectory); }
        catch (Exception ex) { Log($"WARN: DSH rollback cleanup after Ready failed: {ex.Message}"); }
        _stagedStore.TryWriteActivePointer(
            paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken);
    }

    /// <summary>Restore the old current tree after the replacement failed to
    /// become Ready. Returns false only when no usable rollback could be
    /// restored.</summary>
    public bool RollbackAppliedUpdate()
    {
        var paths = Paths;
        if (!Directory.Exists(paths.DshRollbackDirectory))
            return false;
        try
        {
            DeleteDirectory(paths.DshCurrentDirectory);
            MoveDirectory(paths.DshRollbackDirectory, paths.DshCurrentDirectory);
            _stagedStore.TryWriteActivePointer(
                paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken);
            Log($"DSH update rolled back to {CurrentVersion}.");
            return IsInstalled();
        }
        catch (Exception ex)
        {
            Log($"DSH update rollback failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Move the validated scratch tree onto <c>dsh-current</c>. The commit
    /// point is the successful <c>Directory.Move(scratch, current)</c>; after
    /// that the new runtime is live. Rollback cleanup and pointer writes are
    /// best-effort and must not pretend the swap was undone.
    /// </summary>
    internal bool TrySwapToCurrent(RuntimePaths paths, string scratch)
        => TrySwapToCurrent(paths, scratch, preserveRollback: false);

    private bool TrySwapToCurrent(RuntimePaths paths, string scratch, bool preserveRollback)
    {
        var rollback = paths.DshRollbackDirectory;
        var current = paths.DshCurrentDirectory;
        try
        {
            DeleteDirectory(rollback);
            if (Directory.Exists(current))
                MoveDirectory(current, rollback);
            MoveDirectory(scratch, current);
        }
        catch (Exception ex)
        {
            Log($"DSH swap failed: {ex}. Recovering.");
            if (Directory.Exists(rollback) && !Directory.Exists(current))
            {
                try { MoveDirectory(rollback, current); }
                catch (Exception recoveryError)
                {
                    Log($"WARN: DSH rollback restore failed: {recoveryError.Message}");
                }
            }

            return false;
        }

        if (!preserveRollback)
        {
            try { DeleteDirectory(rollback); }
            catch (Exception ex)
            {
                Log($"WARN: DSH leftover rollback cleanup failed: {ex.Message}");
            }
        }

        if (!_stagedStore.TryWriteActivePointer(
                paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken))
            Log("WARN: DSH current is in place but the active pointer could not be written.");

        return true;
    }

    private void RecoverUncommittedUpdate(RuntimePaths paths)
    {
        if (!Directory.Exists(paths.DshRollbackDirectory))
            return;
        Log("Recovering an uncommitted DSH update from the last known-good rollback.");
        if (!RollbackAppliedUpdate())
            Log("WARN: the uncommitted DSH update could not be rolled back during startup.");
    }

    private static bool IsStrictlyNewer(string? candidate, string? current)
    {
        if (!DshSemanticVersion.TryParse(candidate, out var candidateVersion))
            return false;
        if (string.IsNullOrWhiteSpace(current))
            return IsCompatibleVersion(candidate);
        return DshSemanticVersion.TryParse(current, out var currentVersion)
            && candidateVersion.CompareTo(currentVersion) > 0;
    }

    private static void WriteUpdatePackageJson(string directory, string candidate)
    {
        var manifest = new
        {
            name = "psx-dsh-runtime",
            version = "0.1.0",
            @private = true,
            description = "PSX-managed DeepSeek Harness runtime update.",
            dependencies = new Dictionary<string, string>
            {
                [DshPackageName] = candidate
            }
        };
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteUpdateReceipt(string directory, string candidate)
    {
        var receipt = new
        {
            package = DshPackageName,
            version = candidate,
            registry = OfficialRegistry
        };
        File.WriteAllText(
            Path.Combine(directory, UpdateReceiptName),
            JsonSerializer.Serialize(receipt));
    }

    private static string? ReadUpdateReceiptVersion(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, UpdateReceiptName)));
            var root = document.RootElement;
            if (!root.TryGetProperty("package", out var package)
                || package.ValueKind != JsonValueKind.String
                || !string.Equals(package.GetString(), DshPackageName, StringComparison.Ordinal)
                || !root.TryGetProperty("registry", out var registry)
                || registry.ValueKind != JsonValueKind.String
                || !string.Equals(registry.GetString(), OfficialRegistry, StringComparison.Ordinal)
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String)
                return null;
            return version.GetString();
        }
        catch { return null; }
    }

    /// <summary>
    /// Validate the exact requested package and the generated npm lock. npm
    /// already verifies tarball integrity while extracting; this gate also
    /// makes the official registry, exact version and recorded integrity
    /// observable before dsh-next can be activated.
    /// </summary>
    internal static string? ValidateStagedUpdate(string directory, string expectedVersion)
    {
        if (!DshSemanticVersion.TryParse(expectedVersion, out _))
            return "expected version is not valid semver";
        if (!File.Exists(Path.Combine(directory, DshEntryPath)))
            return "DSH entry file is missing";
        var installedVersion = ReadPackageVersion(Path.Combine(directory, DshPackageJson));
        if (!string.Equals(installedVersion, expectedVersion, StringComparison.Ordinal))
            return $"installed version '{installedVersion}' does not match '{expectedVersion}'";
        if (!string.Equals(expectedVersion, SeededPackageVersion, StringComparison.Ordinal)
            && !string.Equals(ReadUpdateReceiptVersion(directory), expectedVersion, StringComparison.Ordinal))
            return "PSX update receipt is missing or invalid";

        var lockPath = Path.Combine(directory, "package-lock.json");
        if (!File.Exists(lockPath))
            return "package-lock.json is missing";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("packages", out var packages)
                || packages.ValueKind != JsonValueKind.Object)
                return "lockfile packages table is missing";
            if (!packages.TryGetProperty("", out var rootPackage)
                || !rootPackage.TryGetProperty("dependencies", out var dependencies)
                || !dependencies.TryGetProperty(DshPackageName, out var requested)
                || requested.ValueKind != JsonValueKind.String
                || !string.Equals(requested.GetString(), expectedVersion, StringComparison.Ordinal))
                return "lockfile root dependency is not exact";

            var dshLockKey = "node_modules/@deepseek-ai/dsh";
            if (!packages.TryGetProperty(dshLockKey, out var dshPackage)
                || !PackageEntryHasOfficialIntegrity(dshPackage, expectedVersion))
                return "DSH lock entry is not pinned to the official registry with integrity";

            foreach (var entry in packages.EnumerateObject())
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                var value = entry.Value;
                if (value.TryGetProperty("link", out var link)
                    && link.ValueKind == JsonValueKind.True)
                    continue;
                // Bundled dependencies are covered by their containing
                // package tarball's integrity. Every other package must carry
                // its own official-registry URL and sha512 integrity; a
                // missing field is not silently treated as trusted.
                if (value.TryGetProperty("inBundle", out var inBundle)
                    && inBundle.ValueKind == JsonValueKind.True)
                    continue;
                if (!value.TryGetProperty("resolved", out var resolved)
                    || resolved.ValueKind != JsonValueKind.String
                    || !IsOfficialRegistryUrl(resolved.GetString())
                    || !value.TryGetProperty("integrity", out var integrity)
                    || integrity.ValueKind != JsonValueKind.String
                    || !integrity.GetString()!.StartsWith("sha512-", StringComparison.Ordinal))
                    return $"lock entry '{entry.Name}' is not registry/integrity pinned";
            }
        }
        catch (Exception ex)
        {
            return $"lockfile could not be parsed: {ex.GetType().Name}";
        }

        return null;
    }

    private static bool PackageEntryHasOfficialIntegrity(JsonElement entry, string expectedVersion) =>
        entry.TryGetProperty("version", out var version)
        && version.ValueKind == JsonValueKind.String
        && string.Equals(version.GetString(), expectedVersion, StringComparison.Ordinal)
        && entry.TryGetProperty("resolved", out var resolved)
        && resolved.ValueKind == JsonValueKind.String
        && IsOfficialRegistryUrl(resolved.GetString())
        && entry.TryGetProperty("integrity", out var integrity)
        && integrity.ValueKind == JsonValueKind.String
        && integrity.GetString()!.StartsWith("sha512-", StringComparison.Ordinal);

    private static bool IsOfficialRegistryUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "registry.npmjs.org", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo);

    internal static string? ParseNpmViewVersion(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        try
        {
            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Build the strictly-newer catalog from an <c>npm view</c> payload.
    /// Accepts either a JSON string (legacy single-version fixture) or an
    /// object with <c>versions</c> + <c>dist-tags</c>. Dist-tags annotate
    /// versions only; they never gate which versions appear.
    /// </summary>
    internal static bool TryBuildUpdateCatalog(
        string stdout,
        string? currentVersion,
        out IReadOnlyList<DshAvailableVersion> catalog,
        out string? error)
    {
        catalog = DshUpdateCheckResult.EmptyVersions;
        error = null;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            error = "empty registry response";
            return false;
        }

        if (!DshSemanticVersion.TryParse(SeededPackageVersion, out var seeded))
        {
            error = "seed version is invalid";
            return false;
        }

        DshSemanticVersion? current = null;
        if (DshSemanticVersion.TryParse(currentVersion, out var parsedCurrent))
            current = parsedCurrent;

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var tagsByVersion = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            if (root.ValueKind == JsonValueKind.String)
            {
                var single = root.GetString();
                if (!DshSemanticVersion.TryParse(single, out var singleVersion)
                    || singleVersion.CompareTo(seeded) < 0
                    || (current.HasValue && singleVersion.CompareTo(current.Value) <= 0))
                {
                    catalog = DshUpdateCheckResult.EmptyVersions;
                    return true;
                }

                catalog = new[] { new DshAvailableVersion(single!, Array.Empty<string>()) };
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "registry response is not an object or string";
                return false;
            }

            if (root.TryGetProperty("dist-tags", out var distTags)
                && distTags.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in distTags.EnumerateObject())
                {
                    if (tag.Value.ValueKind != JsonValueKind.String)
                        continue;
                    var taggedVersion = tag.Value.GetString();
                    if (string.IsNullOrWhiteSpace(taggedVersion))
                        continue;
                    if (!tagsByVersion.TryGetValue(taggedVersion, out var list))
                    {
                        list = new List<string>();
                        tagsByVersion[taggedVersion] = list;
                    }
                    list.Add(tag.Name);
                }
            }

            if (!root.TryGetProperty("versions", out var versionsElement))
            {
                error = "versions array is missing";
                return false;
            }

            var versions = new List<string>();
            if (versionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in versionsElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(entry.GetString()))
                        versions.Add(entry.GetString()!);
                }
            }
            else if (versionsElement.ValueKind == JsonValueKind.Object)
            {
                // npm sometimes returns versions as an object keyed by version.
                foreach (var entry in versionsElement.EnumerateObject())
                    versions.Add(entry.Name);
            }
            else
            {
                error = "versions field has an unexpected shape";
                return false;
            }

            var filtered = new List<(DshSemanticVersion Parsed, DshAvailableVersion Item)>();
            foreach (var version in versions)
            {
                if (!DshSemanticVersion.TryParse(version, out var parsed)
                    || parsed.CompareTo(seeded) < 0
                    || (current.HasValue && parsed.CompareTo(current.Value) <= 0))
                    continue;
                tagsByVersion.TryGetValue(version, out var tags);
                filtered.Add((parsed, new DshAvailableVersion(
                    version,
                    tags is { Count: > 0 } ? tags.ToArray() : Array.Empty<string>())));
            }

            filtered.Sort((left, right) => right.Parsed.CompareTo(left.Parsed));
            catalog = filtered.Select(entry => entry.Item).ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Copy every seed manifest or report the first missing one: an
    /// incomplete seed must hard-fail instead of letting npm ci run without
    /// the lockfile or the registry-pinning .npmrc.
    /// </summary>
    private static string? CopySeedManifests(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
        {
            var src = Path.Combine(source, name);
            if (!File.Exists(src))
                return name;
            File.Copy(src, Path.Combine(destination, name), overwrite: true);
        }
        return null;
    }

    private static string? ReadPackageVersion(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static void DeleteDirectory(string path)
    {
        ExecuteFileSystemOperationWithRetry(
            () =>
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            });
    }

    private static void MoveDirectory(string source, string destination) =>
        ExecuteFileSystemOperationWithRetry(() => Directory.Move(source, destination));

    private static void ExecuteFileSystemOperationWithRetry(Action operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (
                (ex is IOException || ex is UnauthorizedAccessException)
                && attempt < FileSystemRetryDelays.Length)
            {
                // npm and Defender can briefly retain handles after process
                // exit. These bounded retries keep the atomic directory swap
                // reliable without hiding persistent permission failures.
                Thread.Sleep(FileSystemRetryDelays[attempt]);
            }
        }
    }

    private void Log(string message) =>
        RotatingDiagnosticLog.AppendLine(_logPath, message);
}

public sealed record DshLaunchSpec(string NodePath, string EntryPath);
public sealed record DshInstallResult(bool Success, string? ErrorMessage, int? ExitCode);
public sealed record DshAvailableVersion(string Version, IReadOnlyList<string> Tags);
public sealed record DshUpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string? CurrentVersion,
    string? AvailableVersion,
    IReadOnlyList<DshAvailableVersion> AvailableVersions,
    string? ErrorMessage)
{
    public static IReadOnlyList<DshAvailableVersion> EmptyVersions { get; } =
        Array.Empty<DshAvailableVersion>();

    public static DshUpdateCheckResult Failed(string? current, string error) =>
        new(false, false, current, null, EmptyVersions, error);

    public static DshUpdateCheckResult UpToDate(string? current) =>
        new(true, false, current, null, EmptyVersions, null);

    public static DshUpdateCheckResult Available(
        string? current,
        IReadOnlyList<DshAvailableVersion> catalog)
    {
        var list = catalog ?? EmptyVersions;
        return new(
            true,
            list.Count > 0,
            current,
            list.Count > 0 ? list[0].Version : null,
            list,
            null);
    }
}
public sealed record DshUpdateResult(
    bool Success,
    string? PreviousVersion,
    string? CandidateVersion,
    string? ErrorMessage,
    int? ExitCode);

/// <summary>Small, dependency-free SemVer 2 precedence implementation used
/// only for the DSH package gate. Build metadata is ignored for precedence;
/// numeric prerelease identifiers sort before non-numeric identifiers.</summary>
internal readonly record struct DshSemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease) : IComparable<DshSemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out DshSemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var match = Pattern.Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch))
            return false;
        version = new(major, minor, patch,
            match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    public int CompareTo(DshSemanticVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (Prerelease == null) return other.Prerelease == null ? 0 : 1;
        if (other.Prerelease == null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            comparison = CompareIdentifier(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            var length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : string.CompareOrdinal(left, right);
        }
        if (leftNumeric != rightNumeric)
            return leftNumeric ? -1 : 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value) => value.All(char.IsAsciiDigit);
}
