using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>Owns both Pi and its ACP adapter. Live trees never change mid-session.</summary>
public sealed class PiAcpRuntime : IAcpAgentRuntime
{
    internal const string PiPackage = "@earendil-works/pi-coding-agent";
    internal const string AdapterPackage = "pi-acp";
    private readonly RuntimeLocator _locator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NpmRuntimeProcessRunner _runner;
    private readonly StagedRuntimeStore _store;

    public PiAcpRuntime(RuntimeLocator locator, string logDirectory)
        : this(locator, logDirectory, TimeSpan.FromMinutes(10)) { }

    internal PiAcpRuntime(RuntimeLocator locator, string logDirectory, TimeSpan timeout)
    {
        _locator = locator;
        Directory.CreateDirectory(logDirectory);
        LogPath = Path.Combine(logDirectory, "pi-runtime.log");
        _runner = new NpmRuntimeProcessRunner(timeout, Log);
        _store = new StagedRuntimeStore("Pi", Log);
    }

    public event Action<string>? StatusChanged;
    public string LogPath { get; }
    public bool SupportsSelfUpdate => true;
    internal string CurrentDirectory => Path.Combine(_locator.Locate().RuntimeRoot, "pi-current");
    internal string NextDirectory => Path.Combine(_locator.Locate().RuntimeRoot, "pi-next");
    internal string PointerFile => Path.Combine(_locator.Locate().RuntimeRoot, "pi-active.txt");
    private string Launcher => Path.Combine(_locator.Locate().InstallDirectory, "tools", "pi-launcher", "launch.mjs");
    private string Seed => Path.Combine(_locator.Locate().InstallDirectory, "tools", "pi-seed");

    internal string BundledDirectory => Path.Combine(_locator.Locate().InstallDirectory, "tools", "pi");
    private string ActiveDirectory => ValidateRoot(CurrentDirectory) ? CurrentDirectory : BundledDirectory;

    public bool IsReady() => _locator.Locate().PortableNodePath != null
        && File.Exists(Launcher) && ValidateRoot(ActiveDirectory);

    public string BuildStatusText(string? suffix = null) =>
        string.Join(" · ", new[] { IsReady() ? "Pi " + ReadVersion(ActiveDirectory, PiPackage) : "Pi runtime is not installed", suffix }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public RuntimeVersionSnapshot GetVersionSnapshot()
    {
        var pending = _store.PointerSaysNext(PointerFile) && ValidateRoot(NextDirectory);
        return new(ReadVersion(ActiveDirectory, PiPackage), pending ? ReadVersion(NextDirectory, PiPackage) : null, pending)
        {
            ProductName = "Pi",
            TechnicalDetails = ReadVersion(ActiveDirectory, AdapterPackage) is { } version
                ? "pi-acp " + version + (pending ? " → " + ReadVersion(NextDirectory, AdapterPackage) : "") : null
        };
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_store.PointerSaysNext(PointerFile)) return;
            if (ValidateRoot(NextDirectory))
                await PromoteAsync(cancellationToken).ConfigureAwait(false);
            else
                _store.TryWriteActivePointer(_locator.Locate().RuntimeRoot, PointerFile, "current");
        }
        finally { _gate.Release(); }
    }

    public Task<AcpRuntimeOperationResult> EnsureInstalledAsync(CancellationToken cancellationToken = default) =>
        InstallAsync(false, cancellationToken);

    public Task<AcpRuntimeOperationResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        InstallAsync(true, cancellationToken);

    private async Task<AcpRuntimeOperationResult> InstallAsync(bool refresh, CancellationToken cancellationToken)
    {
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(AcpRuntimeOperationKind.Cancelled, "Pi installation cancelled."); }
        try
        {
            if (!refresh && IsReady()) return new(AcpRuntimeOperationKind.AlreadyReady, "Pi is ready.");
            var paths = _locator.Locate();
            if (paths.PortableNodePath == null || paths.PortableNpmCliPath == null || !File.Exists(Launcher)
                || !File.Exists(Path.Combine(Seed, "package-lock.json")))
                return new(AcpRuntimeOperationKind.Failed, "Pi runtime files are missing. Re-extract PSX.");

            var versions = new Dictionary<string, string>();
            if (refresh)
            {
                foreach (var package in new[] { PiPackage, AdapterPackage })
                {
                    var query = await _runner.RunAsync(paths.PortableNodePath, paths.PortableNpmCliPath,
                        Seed, "Pi update check", ["view", package + "@latest", "version", "--registry=https://registry.npmjs.org/"],
                        cancellationToken).ConfigureAwait(false);
                    if (query.Kind != AcpRuntimeOperationKind.Success) return new(query.Kind, query.Message, query.ExitCode);
                    var version = query.Stdout.Trim();
                    var minimum = package == PiPackage ? new Version(0, 85, 1) : new Version(0, 0, 33);
                    if (!VersionAtLeast(version, minimum)
                        || (Version.TryParse(ReadVersion(ActiveDirectory, package), out var current) && !VersionAtLeast(version, current)))
                        return new(AcpRuntimeOperationKind.Failed, "No compatible Pi update was found.");
                    versions.Add(package, version);
                }
                if (IsReady() && versions.All(item => item.Value == ReadVersion(ActiveDirectory, item.Key)))
                    return new(AcpRuntimeOperationKind.AlreadyReady, "Pi is up to date.");
            }

            // Only PSX-owned staging is cleared; current remains usable after any failure.
            if (!_store.TryWriteActivePointer(paths.RuntimeRoot, PointerFile, "current"))
                return new(AcpRuntimeOperationKind.Failed, "Could not reset the Pi update marker.");
            _store.ClearStaleNext(NextDirectory);
            Directory.CreateDirectory(NextDirectory);
            foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
                File.Copy(Path.Combine(Seed, name), Path.Combine(NextDirectory, name), overwrite: true);
            StatusChanged?.Invoke("Installing Pi and pi-acp…");
            IReadOnlyList<string> args = refresh
                ? ["install", PiPackage + "@" + versions[PiPackage], AdapterPackage + "@" + versions[AdapterPackage], "--save-exact", "--omit=dev", "--include=optional", "--engine-strict", "--registry=https://registry.npmjs.org/"]
                : ["ci", "--omit=dev", "--include=optional", "--engine-strict", "--registry=https://registry.npmjs.org/"];
            var result = await _runner.RunAsync(paths.PortableNodePath, paths.PortableNpmCliPath,
                NextDirectory, "Pi installation", args, cancellationToken).ConfigureAwait(false);
            if (result.Kind != AcpRuntimeOperationKind.Success)
                return new(result.Kind, result.Message, result.ExitCode);
            if (!ValidateRoot(NextDirectory))
                return new(AcpRuntimeOperationKind.Failed, "Pi installation validation failed.");
            if (refresh && versions.Any(item => ReadVersion(NextDirectory, item.Key) != item.Value))
                return new(AcpRuntimeOperationKind.Failed, "Pi installed versions do not match the requested update.");
            var smoke = await _runner.RunAsync(paths.PortableNodePath, Launcher, NextDirectory, "Pi validation",
                [Entry(NextDirectory, AdapterPackage, "pi-acp")!, Entry(NextDirectory, PiPackage, "pi")!, "--smoke"],
                cancellationToken).ConfigureAwait(false);
            if (smoke.Kind != AcpRuntimeOperationKind.Success)
                return new(smoke.Kind, "Pi runtime validation failed.", smoke.ExitCode);
            cancellationToken.ThrowIfCancellationRequested();

            if (IsReady())
            {
                if (ReadVersion(ActiveDirectory, PiPackage) == ReadVersion(NextDirectory, PiPackage)
                    && ReadVersion(ActiveDirectory, AdapterPackage) == ReadVersion(NextDirectory, AdapterPackage))
                {
                    _store.ClearStaleNext(NextDirectory);
                    _store.TryWriteActivePointer(paths.RuntimeRoot, PointerFile, "current");
                    return new(AcpRuntimeOperationKind.AlreadyReady, "Pi is up to date.");
                }
                if (!_store.TryWriteActivePointer(paths.RuntimeRoot, PointerFile, "next"))
                    return new(AcpRuntimeOperationKind.Failed, "Could not save the Pi update marker.");
                StatusChanged?.Invoke("Pi update ready. Restart PSX to apply.");
                return new(AcpRuntimeOperationKind.Success, "Pi update ready. Restart PSX to apply.");
            }
            if (!await PromoteAsync(cancellationToken).ConfigureAwait(false))
                return new(AcpRuntimeOperationKind.Failed, "Could not activate Pi.");
            StatusChanged?.Invoke("Pi is ready.");
            return new(AcpRuntimeOperationKind.Success, "Pi is ready.");
        }
        catch (OperationCanceledException) { return new(AcpRuntimeOperationKind.Cancelled, "Pi installation cancelled."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log(ex.ToString());
            return new(AcpRuntimeOperationKind.Failed, "Pi installation failed. Retry the installation.");
        }
        finally { _gate.Release(); }
    }

    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        if (!IsReady()) throw new InvalidOperationException("Pi runtime is not installed.");
        var node = _locator.Locate().PortableNodePath!;
        return new()
        {
            FileName = node,
            WorkingDirectory = workingDirectory,
            Arguments = [Launcher, Entry(ActiveDirectory, AdapterPackage, "pi-acp")!, Entry(ActiveDirectory, PiPackage, "pi")!],
            Environment = new Dictionary<string, string?>
            {
                ["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            }
        };
    }

    private async Task<bool> PromoteAsync(CancellationToken cancellationToken)
    {
        // Windows indexers/scanners can briefly hold a newly validated tree.
        // Each failed store attempt restores current before we retry; never
        // retry an incomplete next tree or bypass the shared rollback path.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidateRoot(NextDirectory)) return false;
            if (_store.PromoteNextToCurrent(_locator.Locate().RuntimeRoot, CurrentDirectory, NextDirectory, PointerFile))
                return true;
            if (attempt < 4)
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    internal string? InteractiveCommand(string? sessionId)
    {
        if (!IsReady()) return null;
        return "& " + Quote(_locator.Locate().PortableNodePath!) + " " + Quote(Entry(ActiveDirectory, PiPackage, "pi")!)
            + (string.IsNullOrWhiteSpace(sessionId) ? "" : " --session " + Quote(sessionId));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string PackageDirectory(string root, string package) => Path.Combine(root, "node_modules", package.Replace('/', Path.DirectorySeparatorChar));

    internal static string? Entry(string root, string package, string bin)
    {
        try
        {
            var directory = Path.GetFullPath(PackageDirectory(root, package));
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
            if (doc.RootElement.GetProperty("name").GetString() != package) return null;
            var value = doc.RootElement.GetProperty("bin");
            var relative = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetProperty(bin).GetString();
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            return full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return null; }
    }

    internal static bool ValidateRoot(string root) => File.Exists(Path.Combine(root, "package-lock.json"))
        && Entry(root, PiPackage, "pi") != null && Entry(root, AdapterPackage, "pi-acp") != null
        && VersionAtLeast(ReadVersion(root, PiPackage), new Version(0, 85, 1))
        && VersionAtLeast(ReadVersion(root, AdapterPackage), new Version(0, 0, 33));

    private static bool VersionAtLeast(string? value, Version minimum) =>
        value != null && Version.TryParse(value, out var version) && version >= minimum;

    private static string? ReadVersion(string root, string package)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageDirectory(root, package), "package.json")));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return null; }
    }

    private void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
    public void Dispose() => _gate.Dispose();
}
