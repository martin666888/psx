using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";
    private static readonly string DshEntryPath =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    private static readonly string DshPackageJson =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "package.json");
    private static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(20);

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

    public bool IsInstalled()
    {
        var paths = Paths;
        return File.Exists(Path.Combine(paths.DshCurrentDirectory, DshPackageJson))
            && File.Exists(Path.Combine(paths.DshCurrentDirectory, DshEntryPath));
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
        if (_stagedStore.PointerSaysNext(paths.DshActivePointerFile, ActiveNextToken))
            _stagedStore.PromoteNextToCurrent(
                paths.RuntimeRoot, paths.DshCurrentDirectory,
                paths.DshNextDirectory, paths.DshActivePointerFile, ActiveCurrentToken);
        _stagedStore.ClearStaleNext(paths.DshNextDirectory);
    }

    /// <summary>
    /// Move the validated scratch tree onto <c>dsh-current</c>. The commit
    /// point is the successful <c>Directory.Move(scratch, current)</c>; after
    /// that the new runtime is live. Rollback cleanup and pointer writes are
    /// best-effort and must not pretend the swap was undone.
    /// </summary>
    internal bool TrySwapToCurrent(RuntimePaths paths, string scratch)
    {
        var rollback = paths.DshRollbackDirectory;
        var current = paths.DshCurrentDirectory;
        try
        {
            DeleteDirectory(rollback);
            if (Directory.Exists(current))
                Directory.Move(current, rollback);
            Directory.Move(scratch, current);
        }
        catch (Exception ex)
        {
            Log($"DSH swap failed: {ex}. Recovering.");
            if (Directory.Exists(rollback) && !Directory.Exists(current))
            {
                try { Directory.Move(rollback, current); }
                catch (Exception recoveryError)
                {
                    Log($"WARN: DSH rollback restore failed: {recoveryError.Message}");
                }
            }

            return false;
        }

        try { DeleteDirectory(rollback); }
        catch (Exception ex)
        {
            Log($"WARN: DSH leftover rollback cleanup failed: {ex.Message}");
        }

        if (!_stagedStore.TryWriteActivePointer(
                paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken))
            Log("WARN: DSH current is in place but the active pointer could not be written.");

        return true;
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
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private void Log(string message) =>
        RotatingDiagnosticLog.AppendLine(_logPath, message);
}

public sealed record DshLaunchSpec(string NodePath, string EntryPath);
public sealed record DshInstallResult(bool Success, string? ErrorMessage, int? ExitCode);
