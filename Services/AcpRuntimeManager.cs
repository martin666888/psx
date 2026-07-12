using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Outcome of an ACP runtime operation. Callers should branch on <see cref="Kind"/>
/// and surface <see cref="Message"/> in the UI.
/// </summary>
public enum AcpRuntimeOperationKind
{
    Success,
    AlreadyReady,
    Failed,
    NetworkUnavailable,
    Cancelled,
}

public sealed record AcpRuntimeOperationResult(
    AcpRuntimeOperationKind Kind,
    string Message,
    int? ExitCode = null);

public sealed record AcpRuntimeVersionInfo(
    string? CurrentAcpVersion,
    string? CurrentClaudeCodeVersion,
    string? PendingAcpVersion,
    bool IsInstalled,
    bool HasPendingUpdate);

/// <summary>
/// Owns the ACP adapter installation across two directories:
///   <c>acp-current</c> — what Agent mode actually loads
///   <c>acp-next</c>    — what background <c>npm update</c> writes to
///
/// The live Agent session reads ONLY from <c>acp-current</c>. Background
/// refresh flips the pending marker (acp-active.txt) to "next"; the next
/// PSX launch promotes acp-next → acp-current, so updates only ever take
/// effect after a full process restart. This eliminates the half-updated
/// node_modules hazard where the foreground Agent could read a node_modules
/// that a background npm update was mid-way through rewriting.
/// </summary>
public sealed class AcpRuntimeManager : IAcpAgentRuntime
{
    private static readonly string AcpAdapterSubpath =
        Path.Combine("node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");
    private static readonly string BundledClaudeCodeSubpath =
        Path.Combine("node_modules", "@anthropic-ai", "claude-agent-sdk-win32-x64", "claude.exe");
    private const string ActiveCurrentToken = "current";
    private const string ActiveNextToken = "next";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;

    /// <summary>
    /// Serializes npm invocations so <c>EnsureInstalledAsync</c> and
    /// <c>RefreshAsync</c> can't both be writing node_modules at once.
    /// Even though they target different directories, holding this for the
    /// whole operation also serializes against the lazy promote on disk.
    /// </summary>
    private readonly SemaphoreSlim _npmLock = new(1, 1);

    /// <summary>
    /// Fired from background threads with human-readable progress messages.
    /// Subscribers (the UI) are responsible for marshalling to the UI thread.
    /// </summary>
    public event Action<string>? StatusChanged;

    public AcpRuntimeManager(RuntimeLocator locator, string logDirectory)
    {
        _locator = locator;
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "acp-runtime.log");
    }

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    public bool IsReady()
    {
        return IsAdapterInstalled() && IsBundledClaudeCodeInstalled();
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        await TryPromoteNextToCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        var paths = Paths;
        var nodePath = paths.PortableNodePath;
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new InvalidOperationException(
                "Portable Node.js was not found next to PSX.exe. The release zip should include tools/node/node.exe.");
        }

        var adapterPath = Path.Combine(paths.AcpActiveDirectory, AcpAdapterSubpath);
        if (!File.Exists(adapterPath))
        {
            throw new InvalidOperationException(
                $"ACP adapter is missing at {paths.AcpActiveDirectory}. See log: {LogPath}");
        }

        return new AcpProcessSpec
        {
            FileName = nodePath,
            WorkingDirectory = paths.AcpActiveDirectory,
            Arguments = new[] { adapterPath }
        };
    }

    /// <summary>True when the active adapter JS entry point is present on disk.</summary>
    public bool IsAdapterInstalled()
    {
        return File.Exists(Path.Combine(Paths.AcpActiveDirectory, AcpAdapterSubpath));
    }

    /// <summary>True when the bundled Claude Code binary is present in the active dir.</summary>
    public bool IsBundledClaudeCodeInstalled()
    {
        return File.Exists(Path.Combine(Paths.AcpActiveDirectory, BundledClaudeCodeSubpath));
    }

    public AcpRuntimeVersionInfo GetVersionInfo()
    {
        var paths = Paths;
        var isInstalled = IsAdapterCompleteInDirectory(paths.AcpCurrentDirectory)
            && IsBundledClaudeCodeCompleteInDirectory(paths.AcpCurrentDirectory);
        var hasPendingUpdate = PointerSaysNext(paths)
            && IsAdapterCompleteInDirectory(paths.AcpNextDirectory)
            && IsBundledClaudeCodeCompleteInDirectory(paths.AcpNextDirectory);

        return new AcpRuntimeVersionInfo(
            CurrentAcpVersion: isInstalled ? ReadPackageVersion(paths.AcpCurrentDirectory, "@agentclientprotocol", "claude-agent-acp") : null,
            CurrentClaudeCodeVersion: isInstalled ? ReadClaudeCodeVersion(paths.AcpCurrentDirectory) : null,
            PendingAcpVersion: hasPendingUpdate ? ReadPackageVersion(paths.AcpNextDirectory, "@agentclientprotocol", "claude-agent-acp") : null,
            IsInstalled: isInstalled,
            HasPendingUpdate: hasPendingUpdate);
    }

    public string BuildStatusText(string? suffix = null)
    {
        var info = GetVersionInfo();
        if (!info.IsInstalled)
            return "ACP 未安装 · 首次发送消息时安装";

        var acpVersion = string.IsNullOrWhiteSpace(info.CurrentAcpVersion)
            ? "未知"
            : info.CurrentAcpVersion;
        var claudeCodeVersion = string.IsNullOrWhiteSpace(info.CurrentClaudeCodeVersion)
            ? "未知"
            : info.CurrentClaudeCodeVersion;
        var text = $"当前使用：ACP {acpVersion} · Claude Code {claudeCodeVersion}";

        return string.IsNullOrWhiteSpace(suffix)
            ? text
            : $"{text} · {suffix}";
    }

    /// <summary>
    /// First-run install. Writes into <c>acp-current</c> and ensures the
    /// active pointer is "current" (the default if the file is absent).
    /// Idempotent: returns <see cref="AcpRuntimeOperationKind.AlreadyReady"/>
    /// if both the adapter and bundled claude.exe are already on disk.
    /// </summary>
    public async Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsAdapterInstalled() && IsBundledClaudeCodeInstalled())
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                "ACP runtime is already installed.");
        }

        var writeFailure = await CheckRuntimeDirectoryWritableAsync(cancellationToken).ConfigureAwait(false);
        if (writeFailure != null)
            return writeFailure;

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureInstalledCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _npmLock.Release();
        }
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

    Task<AcpRuntimeOperationResult> IAcpAgentRuntime.EnsureInstalledAsync(
        CancellationToken cancellationToken)
    {
        return EnsureInstalledAsync(cancellationToken: cancellationToken);
    }

    private async Task<AcpRuntimeOperationResult> EnsureInstalledCoreAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (IsAdapterInstalled() && IsBundledClaudeCodeInstalled())
        {
            StatusChanged?.Invoke(BuildStatusText());
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                "ACP runtime is already installed.");
        }

        StatusChanged?.Invoke("正在安装 ACP Adapter");

        var paths = Paths;
        if (paths.PortableNodePath == null)
        {
            StatusChanged?.Invoke("ACP 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                "No node runtime available; cannot install ACP adapter.");
        }

        try
        {
            Directory.CreateDirectory(paths.RuntimeRoot);
            Directory.CreateDirectory(paths.AcpCurrentDirectory);
            // Wipe any stale "next" contents from a prior failed update.
            // Otherwise `npm ci` would refuse to install a non-empty next
            // dir (npm ci requires an absent or fully-aligned node_modules).
            if (Directory.Exists(paths.AcpNextDirectory))
            {
                try { Directory.Delete(paths.AcpNextDirectory, recursive: true); }
                catch (Exception ex) { Log($"WARN: could not clear stale acp-next: {ex.Message}"); }
            }

            SeedDirectoryFromInstallDirectory(paths, paths.AcpCurrentDirectory);
        }
        catch (Exception ex)
        {
            Log($"Failed to seed runtime: {ex}");
            StatusChanged?.Invoke("ACP 安装失败，Agent 暂不可用，请检查网络后重试");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                $"Failed to seed runtime: {ex.Message}");
        }

        // First install: use `npm ci` so we install EXACTLY what the seed
        // lockfile pinned. Fall back to `npm install` only for a local npm/lock
        // failure; cancellation and network failures return immediately.
        var ciResult = await RunNpmCiAsync(paths, paths.AcpCurrentDirectory, progress, cancellationToken).ConfigureAwait(false);
        if (ciResult.Kind == AcpRuntimeOperationKind.Success
            || ciResult.Kind == AcpRuntimeOperationKind.AlreadyReady)
        {
            WriteActivePointer(paths, ActiveCurrentToken);
            StatusChanged?.Invoke(BuildStatusText());
            return ciResult;
        }

        if (ciResult.Kind is AcpRuntimeOperationKind.Cancelled
            or AcpRuntimeOperationKind.NetworkUnavailable)
        {
            StatusChanged?.Invoke(ciResult.Kind == AcpRuntimeOperationKind.Cancelled
                ? "ACP 安装已取消"
                : "ACP 安装失败，Agent 暂不可用，请检查网络后重试");
            return ciResult;
        }

        Log($"npm ci failed ({ciResult.Message}); falling back to npm install.");
        var installResult = await RunNpmAsync(paths, paths.AcpCurrentDirectory, "install", progress, cancellationToken).ConfigureAwait(false);
        if (installResult.Kind == AcpRuntimeOperationKind.Success)
        {
            WriteActivePointer(paths, ActiveCurrentToken);
            StatusChanged?.Invoke(BuildStatusText());
        }
        else
        {
            StatusChanged?.Invoke("ACP 安装失败，Agent 暂不可用，请检查网络后重试");
        }
        return installResult;
    }

    /// <summary>
    /// Background refresh. Writes into <c>acp-next</c> only — never touches
    /// <c>acp-current</c>, which the live Agent session is reading from.
    /// On success, flips the active pointer to "next"; the next PSX launch
    /// will load from acp-next. On failure, leaves the pointer alone so
    /// the user keeps using the previously-working version.
    /// </summary>
    public async Task<AcpRuntimeOperationResult> RefreshAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdapterInstalled())
        {
            StatusChanged?.Invoke("ACP 未安装 · 首次发送消息时安装");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady,
                "ACP runtime is not yet installed; skipping background refresh.");
        }

        var paths = Paths;
        if (paths.PortableNodePath == null)
        {
            StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
            return new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.Failed,
                "No node runtime available; cannot refresh ACP adapter.");
        }

        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StatusChanged?.Invoke(BuildStatusText("正在检查更新"));
            // Refresh target is acp-next, NOT acp-current. Prepare a clean
            // staging dir: clear any leftover from a previous failed update,
            // copy manifests from seed (so lock + .npmrc are correct), then
            // run npm update.
            try
            {
                if (Directory.Exists(paths.AcpNextDirectory))
                    Directory.Delete(paths.AcpNextDirectory, recursive: true);
                Directory.CreateDirectory(paths.AcpNextDirectory);
                SeedDirectoryFromInstallDirectory(paths, paths.AcpNextDirectory);
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare acp-next: {ex}");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    $"Failed to prepare acp-next: {ex.Message}");
            }

            // Seed the package.json's version range into acp-next, but PRESERVE
            // any user-edited acp-next/package.json if the user previously
            // pinned to a specific version. Same rule as before.
            if (!File.Exists(Path.Combine(paths.AcpNextDirectory, "package.json")))
            {
                var seedPkg = Path.Combine(paths.AcpSeedDirectory, "package.json");
                if (File.Exists(seedPkg))
                {
                    File.Copy(seedPkg, Path.Combine(paths.AcpNextDirectory, "package.json"));
                }
            }

            var updateResult = await RunNpmAsync(
                paths,
                paths.AcpNextDirectory,
                "update",
                progress,
                cancellationToken,
                "@agentclientprotocol/claude-agent-acp").ConfigureAwait(false);

            if (updateResult.Kind != AcpRuntimeOperationKind.Success)
            {
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
                return updateResult;
            }

            // Validate the freshly-updated adapter is structurally complete
            // before flipping the pointer. This guards against partial writes
            // (npm update exit 0 but a file is still being flushed).
            if (!IsAdapterCompleteInDirectory(paths.AcpNextDirectory))
            {
                Log("Post-update validation failed: adapter entry point missing in acp-next.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "Updated adapter is missing its entry point; not switching to it.");
            }
            if (!IsBundledClaudeCodeCompleteInDirectory(paths.AcpNextDirectory))
            {
                Log("Post-update validation failed: bundled claude.exe missing in acp-next.");
                StatusChanged?.Invoke(BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
                return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                    "Updated adapter is missing the bundled claude.exe; not switching to it.");
            }

            var currentVersion = ReadPackageVersion(paths.AcpCurrentDirectory, "@agentclientprotocol", "claude-agent-acp");
            var pendingVersion = ReadPackageVersion(paths.AcpNextDirectory, "@agentclientprotocol", "claude-agent-acp");
            if (!string.IsNullOrWhiteSpace(currentVersion)
                && string.Equals(currentVersion, pendingVersion, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(paths.AcpNextDirectory, recursive: true); }
                catch (Exception ex) { Log($"WARN: could not clear unchanged acp-next: {ex.Message}"); }
                WriteActivePointer(paths, ActiveCurrentToken);
                StatusChanged?.Invoke(BuildStatusText());
                return updateResult;
            }

            WriteActivePointer(paths, ActiveNextToken);
            Log($"acp-next updated and verified; active pointer flipped to 'next'. " +
                "Next PSX launch will use the new version.");
            StatusChanged?.Invoke(BuildStatusText(
                string.IsNullOrWhiteSpace(pendingVersion)
                    ? "已更新，下次启动生效"
                    : $"已更新到 ACP {pendingVersion}，下次启动生效"));
            return updateResult;
        }
        finally
        {
            _npmLock.Release();
        }
    }

    Task<AcpRuntimeOperationResult> IAcpAgentRuntime.RefreshAsync(
        CancellationToken cancellationToken)
    {
        return RefreshAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Promote acp-next to acp-current on PSX startup when the pointer was
    /// flipped to "next" by a previous background refresh.
    ///
    /// Runs under <see cref="_npmLock"/> so it cannot collide with a
    /// background <c>RefreshAsync</c> that is still writing acp-next. The
    /// caller must re-resolve <see cref="Paths"/> after this method
    /// returns — the directory layout on disk has changed.
    /// </summary>
    public async Task<bool> TryPromoteNextToCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _npmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths;

            // Gate on the pointer. A stale or partial acp-next (left over
            // from a failed update, or a directory the user created by
            // hand) must NOT be promoted unless the previous background
            // refresh explicitly flipped pointer → "next".
            if (!PointerSaysNext(paths))
            {
                // Pointer already says "current", or the file is missing /
                // unreadable. Nothing to do; make sure the file is well-formed.
                WriteActivePointer(paths, ActiveCurrentToken);
                return false;
            }

            if (!Directory.Exists(paths.AcpNextDirectory))
            {
                // Pointer says next but there's no next directory — should
                // never happen if the refresh code is correct, but handle
                // it: revert the pointer so the user is on a known state.
                Log("Lazy promote: pointer='next' but acp-next missing; reverting.");
                WriteActivePointer(paths, ActiveCurrentToken);
                return false;
            }

            if (!IsAdapterCompleteInDirectory(paths.AcpNextDirectory)
                || !IsBundledClaudeCodeCompleteInDirectory(paths.AcpNextDirectory))
            {
                Log("Lazy promote aborted: acp-next is incomplete; reverting pointer to 'current'.");
                WriteActivePointer(paths, ActiveCurrentToken);
                return false;
            }

            // Swap. Order matters: move the live acp-current aside first so
            // there is always exactly one of {current, next} with valid
            // contents. If we crash mid-swap, the pointer is "next" but the
            // directory layout is the original; the next launch retries.
            var backup = paths.AcpCurrentDirectory + ".old";
            try
            {
                if (Directory.Exists(backup))
                    Directory.Delete(backup, recursive: true);

                if (Directory.Exists(paths.AcpCurrentDirectory))
                    Directory.Move(paths.AcpCurrentDirectory, backup);

                Directory.Move(paths.AcpNextDirectory, paths.AcpCurrentDirectory);
                Directory.Delete(backup, recursive: true);
            }
            catch (Exception ex)
            {
                Log($"Lazy promote failed mid-swap: {ex}. Recovering.");
                if (Directory.Exists(backup) && !Directory.Exists(paths.AcpCurrentDirectory))
                {
                    try { Directory.Move(backup, paths.AcpCurrentDirectory); } catch { /* give up */ }
                }
                WriteActivePointer(paths, ActiveCurrentToken);
                return false;
            }

            WriteActivePointer(paths, ActiveCurrentToken);
            Log("Lazy promote: acp-next is now acp-current.");
            StatusChanged?.Invoke(BuildStatusText());
            return true;
        }
        finally
        {
            _npmLock.Release();
        }
    }

    private bool PointerSaysNext(RuntimePaths paths)
    {
        try
        {
            if (!File.Exists(paths.AcpActivePointerFile))
                return false;
            var token = File.ReadAllText(paths.AcpActivePointerFile).Trim();
            return string.Equals(token, ActiveNextToken, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // ---- internals ----

    private void SeedDirectoryFromInstallDirectory(RuntimePaths paths, string destination)
    {
        // We only copy manifest files (package.json, package-lock.json, .npmrc).
        // node_modules is filled by `npm ci` — never copy it; it's 100s of MB.
        foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
        {
            var source = Path.Combine(paths.AcpSeedDirectory, name);
            if (!File.Exists(source)) continue;

            var dest = Path.Combine(destination, name);
            // Skip package.json if destination already has one — preserve any
            // user-edited version range. lock + .npmrc always come from seed
            // so the runtime never pins a stale or wrong-mirror lock.
            if (File.Exists(dest) && name == "package.json")
                continue;

            File.Copy(source, dest, overwrite: true);
            Log($"Seeded {name} -> {dest}");
        }
    }

    private bool IsAdapterCompleteInDirectory(string directory)
    {
        return File.Exists(Path.Combine(directory, AcpAdapterSubpath));
    }

    private bool IsBundledClaudeCodeCompleteInDirectory(string directory)
    {
        return File.Exists(Path.Combine(directory, BundledClaudeCodeSubpath));
    }

    private static string? ReadPackageVersion(string rootDirectory, params string[] packagePathParts)
    {
        return ReadPackageJsonString(rootDirectory, packagePathParts, "version");
    }

    private static string? ReadClaudeCodeVersion(string rootDirectory)
    {
        var sdkPackageParts = new[] { "@anthropic-ai", "claude-agent-sdk" };
        return ReadPackageJsonString(rootDirectory, sdkPackageParts, "claudeCodeVersion")
            ?? ReadPackageJsonString(rootDirectory, sdkPackageParts, "claude_code_version")
            ?? ReadPackageJsonString(rootDirectory, sdkPackageParts, "version");
    }

    private static string? ReadPackageJsonString(string rootDirectory, string[] packagePathParts, string propertyName)
    {
        try
        {
            var parts = new List<string> { rootDirectory, "node_modules" };
            parts.AddRange(packagePathParts);
            parts.Add("package.json");
            var packageJsonPath = Path.Combine(parts.ToArray());
            if (!File.Exists(packageJsonPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch
        {
            // Version display is best-effort; install/update validation is file-based.
        }

        return null;
    }

    private void WriteActivePointer(RuntimePaths paths, string token)
    {
        // Atomic write: write to a sibling temp file, then rename. fsync is
        // best-effort — without it a power loss could leave a half-written
        // pointer, but the contents would still be either the old or new
        // value, never partial bytes (Windows flushes MoveFile atomically
        // within the same volume).
        try
        {
            Directory.CreateDirectory(paths.RuntimeRoot);
            var tmp = paths.AcpActivePointerFile + ".tmp";
            File.WriteAllText(tmp, token);
            File.Move(tmp, paths.AcpActivePointerFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"Failed to write active pointer '{token}': {ex}");
        }
    }

    private async Task<AcpRuntimeOperationResult> RunNpmCiAsync(
        RuntimePaths paths,
        string workingDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (paths.PortableNpmCliPath == null)
        {
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                "npm CLI not found next to node.exe; cannot run npm.");
        }

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

        // npm ci is strict: requires lockfile to exist; fails if lock +
        // package.json disagree. We seeded a lock, so precondition is met.
        startInfo.ArgumentList.Add(paths.PortableNpmCliPath);
        startInfo.ArgumentList.Add("ci");
        startInfo.ArgumentList.Add("--include=optional");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");

        const string label = "npm ci";
        Log($"Starting: {label} in {workingDirectory}");
        progress?.Report("正在安装 ACP Adapter");
        StatusChanged?.Invoke("正在安装 ACP Adapter");

        return await RunNpmProcessAsync(startInfo, label, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcpRuntimeOperationResult> RunNpmAsync(
        RuntimePaths paths,
        string workingDirectory,
        string command,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? packageName = null)
    {
        if (paths.PortableNpmCliPath == null)
        {
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                "npm CLI not found next to node.exe; cannot run npm.");
        }

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

        startInfo.ArgumentList.Add(paths.PortableNpmCliPath);
        startInfo.ArgumentList.Add(command);
        if (!string.IsNullOrEmpty(packageName))
            startInfo.ArgumentList.Add(packageName);
        startInfo.ArgumentList.Add("--include=optional");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");
        // install is only used as a fallback when npm ci fails. Keep the
        // lockfile untouched so the user can recover by retrying. update
        // lets npm advance the lock so users pick up new acp versions.
        if (command == "install")
        {
            startInfo.ArgumentList.Add("--package-lock=false");
        }

        var label = packageName != null
            ? $"npm {command} {packageName}"
            : $"npm {command}";

        Log($"Starting: {label} in {workingDirectory}");
        var status = command == "update"
            ? BuildStatusText("正在更新 ACP")
            : "正在安装 ACP Adapter";
        progress?.Report(status);
        StatusChanged?.Invoke(status);

        return await RunNpmProcessAsync(startInfo, label, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcpRuntimeOperationResult> RunNpmProcessAsync(
        ProcessStartInfo startInfo,
        string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log($"Failed to start {label}: {ex}");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                $"Failed to start {label}: {ex.Message}");
        }

        if (process == null)
        {
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                $"{label} did not start.");
        }
        using var processLifetime = process;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProcessTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            Log($"{label} timed out after {ProcessTimeout.TotalMinutes:0} minutes.");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed,
                $"{label} timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            Log($"{label} was cancelled.");
            return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Cancelled,
                $"{label} was cancelled.");
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
            return new AcpRuntimeOperationResult(kind,
                $"{label} failed with exit code {exitCode}.", exitCode);
        }

        progress?.Report($"{label} completed.");
        return new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Success,
            $"{label} completed successfully.");
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
            // Disk full / file locked — swallow; logs are best-effort.
        }
    }

    public void Dispose()
    {
        _npmLock.Dispose();
    }
}
