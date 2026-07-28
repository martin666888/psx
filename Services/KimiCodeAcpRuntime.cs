using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Runtime for the bundled Kimi Code CLI. Unlike <see cref="AcpRuntimeManager"/>
/// (which installs the Claude ACP adapter on demand via <c>npm ci</c>), Kimi Code
/// is MIT-licensed and pre-installed at build time into
/// <c>{InstallDir}/tools/kimi/</c>. There is therefore no download / promote /
/// current-next machinery here: readiness is purely a validation of the bundled
/// files, and <see cref="RefreshAsync"/> is a no-op in v1 (no self-update).
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

    private static readonly string KimiPackageDirSubpath =
        Path.Combine("node_modules", "@moonshot-ai", "kimi-code");
    private static readonly string KimiPackageJsonSubpath =
        Path.Combine(KimiPackageDirSubpath, "package.json");

    private readonly RuntimeLocator _locator;
    private readonly string _logPath;

    /// <summary>
    /// Fired with human-readable status messages. Subscribers (the UI) are
    /// responsible for marshalling to the UI thread.
    /// </summary>
    public event Action<string>? StatusChanged;

    public KimiCodeAcpRuntime(RuntimeLocator locator, string logDirectory)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "kimi-runtime.log");
    }

    public string LogPath => _logPath;

    public RuntimePaths Paths => _locator.Locate();

    /// <summary>Kimi Code ships inside the PSX release; it never self-updates.</summary>
    public bool SupportsSelfUpdate => false;

    public RuntimeVersionSnapshot GetVersionSnapshot() =>
        new(CurrentVersion: ReadKimiVersion(), PendingVersion: null, HasPendingUpdate: false);

    /// <summary>
    /// True only when the full bundled install is structurally valid: portable
    /// Node, the pinned lockfile, the Kimi package manifest, a resolvable
    /// <c>bin.kimi</c> entry that stays inside the package directory, and the
    /// resolved entry file itself. Missing Git Bash does NOT make this false —
    /// that is a separate Windows prerequisite surfaced via status text.
    /// </summary>
    public bool IsReady()
    {
        return ValidateInstall(out _, out _) == null;
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        // Bundled, pre-installed runtime: nothing to promote or migrate.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        var validationError = ValidateInstall(out var paths, out var entryPath);
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
        var version = ReadKimiVersion();
        var text = string.IsNullOrWhiteSpace(version)
            ? "Kimi Code（随包内置）"
            : $"Kimi Code {version}（随包内置）";

        if (!IsGitBashAvailable())
            text = $"{text} · 缺少 Kimi Code Windows 前置条件（未检测到 Git Bash）";

        return string.IsNullOrWhiteSpace(suffix)
            ? text
            : $"{text} · {suffix}";
    }

    /// <summary>
    /// Kimi Code ships inside the release, so there is nothing to install. This
    /// method validates the bundled files and returns <see cref="AcpRuntimeOperationKind.Failed"/>
    /// (never a fake no-op success) when they are missing or corrupt, so the UI
    /// can prompt the user to re-extract / re-download PSX.
    /// </summary>
    public Task<AcpRuntimeOperationResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateInstall(out var paths, out _);
        if (validationError == null)
        {
            StatusChanged?.Invoke(BuildStatusText());
            return Task.FromResult(new AcpRuntimeOperationResult(
                AcpRuntimeOperationKind.AlreadyReady,
                "Kimi Code runtime is bundled and ready."));
        }

        Log($"Bundled Kimi Code runtime is invalid: {validationError}");
        var message =
            $"随包内置的 Kimi Code 运行时文件缺失或损坏，请重新解压或重新下载 PSX。({validationError})";
        StatusChanged?.Invoke(message);
        return Task.FromResult(new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.Failed, message));
    }

    /// <summary>v1: no self-update for the bundled Kimi Code runtime.</summary>
    public Task<AcpRuntimeOperationResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AcpRuntimeOperationResult(
            AcpRuntimeOperationKind.AlreadyReady,
            "Kimi Code is bundled with PSX; no background refresh is performed."));
    }

    // ---- internals ----

    /// <summary>
    /// Validates the bundled install. Returns <c>null</c> when everything is
    /// present and consistent; otherwise a human-readable reason. On success,
    /// <paramref name="entryPath"/> is the absolute path to the ACP entry file.
    /// </summary>
    private string? ValidateInstall(out RuntimePaths paths, out string? entryPath)
    {
        paths = Paths;
        entryPath = null;

        if (string.IsNullOrWhiteSpace(paths.PortableNodePath) || !File.Exists(paths.PortableNodePath))
            return "portable Node.js (tools/node/node.exe) is missing";

        var kimiRoot = paths.BundledKimiDirectory;
        if (!Directory.Exists(kimiRoot))
            return $"bundled Kimi directory is missing at {kimiRoot}";

        var lockPath = Path.Combine(kimiRoot, PackageLockName);
        if (!File.Exists(lockPath))
            return $"{PackageLockName} is missing under tools/kimi";

        var packageDir = Path.Combine(kimiRoot, KimiPackageDirSubpath);
        var packageJsonPath = Path.Combine(kimiRoot, KimiPackageJsonSubpath);
        if (!File.Exists(packageJsonPath))
            return $"{KimiPackageName} package.json is missing under tools/kimi/node_modules";

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

    /// <summary>
    /// Extracts the Kimi bin entry from a package.json root. Supports both the
    /// object form (<c>"bin": { "kimi": "dist/main.mjs" }</c>) and the string
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

    private string? ReadKimiVersion()
    {
        try
        {
            var packageJsonPath = Path.Combine(Paths.BundledKimiDirectory, KimiPackageJsonSubpath);
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
        // No unmanaged resources; nothing to release.
    }
}
