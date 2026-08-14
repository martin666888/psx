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
        CopySeedManifests(paths.DshSeedDirectory, scratch);

        var result = await _npmRunner.RunAsync(
            paths.PortableNodePath, paths.PortableNpmCliPath, scratch,
            "DSH install", new[] { "ci" }, cancellationToken).ConfigureAwait(false);

        if (result.Kind != AcpRuntimeOperationKind.Success)
        {
            var msg = result.Kind == AcpRuntimeOperationKind.NetworkUnavailable
                ? "网络不可用，无法连接 npm 仓库。请检查网络后重试。"
                : $"安装失败：{(string.IsNullOrWhiteSpace(result.Message) ? "npm ci 返回非零退出码" : result.Message)}";
            return new(false, msg, result.ExitCode);
        }
        if (!File.Exists(Path.Combine(scratch, DshEntryPath)))
            return new(false, "安装后入口文件缺失 (lib/bin.js)。", null);
        var version = ReadPackageVersion(Path.Combine(scratch, DshPackageJson));
        if (string.IsNullOrWhiteSpace(version))
            return new(false, "安装后无法读取 DSH 版本。", null);

        SwapToCurrent(paths, scratch);
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

    private void SwapToCurrent(RuntimePaths paths, string scratch)
    {
        var rollback = paths.DshRollbackDirectory;
        var current = paths.DshCurrentDirectory;
        try
        {
            DeleteDirectory(rollback);
            if (Directory.Exists(current)) Directory.Move(current, rollback);
            Directory.Move(scratch, current);
            DeleteDirectory(rollback);
        }
        catch (Exception ex)
        {
            Log($"DSH swap failed: {ex}. Recovering.");
            if (Directory.Exists(rollback) && !Directory.Exists(current))
                try { Directory.Move(rollback, current); } catch { }
        }
        _stagedStore.TryWriteActivePointer(paths.RuntimeRoot, paths.DshActivePointerFile, ActiveCurrentToken);
    }

    private static void CopySeedManifests(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
        {
            var src = Path.Combine(source, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(destination, name), overwrite: true);
        }
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

    private void Log(string message)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}"); }
        catch { }
    }
}

public sealed record DshLaunchSpec(string NodePath, string EntryPath);
public sealed record DshInstallResult(bool Success, string? ErrorMessage, int? ExitCode);
