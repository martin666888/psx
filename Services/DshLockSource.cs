using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PSX.Services;

/// <summary>Outcome of a bundled-lock lookup. Only
/// <see cref="VersionNotBundled"/> is a normal catalog miss (the version
/// becomes deferred or blocked for the user); the two corrupt kinds mean the
/// PSX installation itself is damaged and must be reinstalled.</summary>
internal enum DshLockLookup
{
    Found,
    VersionNotBundled,
    CatalogInvalid,
    ArtifactCorrupt
}

internal sealed record DshLockLookupResult(DshLockLookup Kind, DshLockArtifact? Artifact = null)
{
    public static DshLockLookupResult Found(DshLockArtifact artifact) =>
        new(DshLockLookup.Found, artifact);

    public static DshLockLookupResult Miss(DshLockLookup kind) => new(kind, null);
}

/// <summary>Directory-level catalog view for the update-check partition. When
/// <see cref="Valid"/> is false the whole check must fail with the reinstall
/// message instead of silently deferring everything.</summary>
internal sealed record DshCatalogSnapshot(
    bool Valid,
    IReadOnlyList<string> Versions,
    IReadOnlyList<string> BlockedVersions)
{
    public static DshCatalogSnapshot Invalid { get; } = new(
        false, Array.Empty<string>(), Array.Empty<string>());
}

internal enum DshLockSourceKind
{
    Bundled
}

/// <summary>A fully verified, repo-trusted lock artifact. <see cref="DshSri"/>
/// is the generation-time official dist.integrity recorded in the catalog;
/// the client never contacts the official registry at update time.</summary>
internal sealed record DshLockArtifact(
    string Version,
    string PackageJson,
    string LockJson,
    string LockSha256,
    string PackageSha256,
    string DshSri,
    DshLockSourceKind Kind);

/// <summary>
/// The seam between DSH updates and where trusted lockfiles come from.
/// Phase A ships the bundled repository catalog; a future phase may add an
/// npm-delivered catalog behind the same interface without touching
/// <see cref="DshWebRuntime"/>.
/// </summary>
internal interface IDshLockSource
{
    /// <summary>Cheap catalog view for the check-time partition; never reads
    /// or hashes lock file bodies.</summary>
    DshCatalogSnapshot LoadCatalog();

    /// <summary>Full verification (files present, size-capped, SHA-matched)
    /// with a reason for every non-Found outcome.</summary>
    DshLockLookupResult Find(string version);
}

/// <summary>
/// Reads the catalog shipped under <c>tools/dsh-locks/</c>. Defense-in-depth
/// against a damaged or tampered installation: version input is semver-gated,
/// resolved paths must stay inside the locks directory (checked with
/// <see cref="Path.GetRelativePath"/>, not string prefixes), every read is
/// size-capped, and artifacts are SHA-verified before they leave the source
/// — the update stage re-verifies after writing them into dsh-next.
/// </summary>
internal sealed class BundledDshLockSource(string directory) : IDshLockSource
{
    /// <summary>Fixed wire-safe review-decision keys; a catalog carrying any
    /// other reason is structurally corrupt.</summary>
    internal static readonly string[] BlockedReasons =
        ["smoke_failed", "official_integrity_mismatch", "sri_conflict"];

    private const int MaxCatalogBytes = 128 * 1024;
    private const int MaxArtifactBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 16;
    private const int MaxBlocked = 10;

    private string DirectoryPath { get; } =
        Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));

    public DshCatalogSnapshot LoadCatalog()
    {
        if (!TryReadCatalog(out var entries, out var blocked))
            return DshCatalogSnapshot.Invalid;
        return new DshCatalogSnapshot(
            true,
            entries.Select(entry => entry.Version).ToArray(),
            blocked);
    }

    public DshLockLookupResult Find(string version)
    {
        if (!DshSemanticVersion.TryParse(version, out _))
            return DshLockLookupResult.Miss(DshLockLookup.VersionNotBundled);
        if (!TryReadCatalog(out var entries, out _))
            return DshLockLookupResult.Miss(DshLockLookup.CatalogInvalid);

        var entry = entries.FirstOrDefault(
            candidate => string.Equals(candidate.Version, version, StringComparison.Ordinal));
        if (entry.Version is null)
            return DshLockLookupResult.Miss(DshLockLookup.VersionNotBundled);

        var versionDirectory = Path.Combine(DirectoryPath, "locks", version);
        var relative = Path.GetRelativePath(DirectoryPath, Path.GetFullPath(versionDirectory));
        if (string.IsNullOrEmpty(relative)
            || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            return DshLockLookupResult.Miss(DshLockLookup.ArtifactCorrupt);

        var lockPath = Path.Combine(versionDirectory, "package-lock.json");
        var packagePath = Path.Combine(versionDirectory, "package.json");
        if (!TryReadVerifiedFile(lockPath, entry.LockSha256, MaxArtifactBytes, out var lockJson)
            || !TryReadVerifiedFile(packagePath, entry.PackageSha256, MaxArtifactBytes, out var packageJson))
            return DshLockLookupResult.Miss(DshLockLookup.ArtifactCorrupt);
        if (!DshSri.TryParse(entry.DshSri, out _))
            return DshLockLookupResult.Miss(DshLockLookup.ArtifactCorrupt);

        return DshLockLookupResult.Found(new DshLockArtifact(
            entry.Version,
            packageJson,
            lockJson,
            entry.LockSha256,
            entry.PackageSha256,
            entry.DshSri,
            DshLockSourceKind.Bundled));
    }

    private bool TryReadCatalog(
        out IReadOnlyList<(string Version, string LockSha256, string PackageSha256, string DshSri)> entries,
        out IReadOnlyList<string> blocked)
    {
        entries = Array.Empty<(string, string, string, string)>();
        blocked = Array.Empty<string>();
        try
        {
            var catalogPath = Path.Combine(DirectoryPath, "catalog.json");
            var info = new FileInfo(catalogPath);
            if (!info.Exists || info.Length > MaxCatalogBytes)
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != 1)
                return false;

            // The whole catalog must be structurally sound — a damaged file
            // fails every check with catalog_corrupt instead of silently
            // degrading to an empty view.
            if (!root.TryGetProperty("entries", out var entriesElement)
                || entriesElement.ValueKind != JsonValueKind.Array
                || entriesElement.GetArrayLength() > MaxEntries)
                return false;

            var parsedEntries = new List<(string Version, string LockSha256, string PackageSha256, string DshSri)>();
            var seenVersions = new HashSet<string>(StringComparer.Ordinal);
            DshSemanticVersion previousVersion = default;
            foreach (var element in entriesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryGetStringProperty(element, "version", out var version)
                    || !DshSemanticVersion.TryParse(version, out var parsedVersion)
                    || !IsAtLeastSeed(parsedVersion)
                    || !seenVersions.Add(version)
                    || !TryGetStringProperty(element, "lockSha256", out var lockSha256)
                    || !IsSha256Hex(lockSha256)
                    || !TryGetStringProperty(element, "packageSha256", out var packageSha256)
                    || !IsSha256Hex(packageSha256)
                    || !TryGetStringProperty(element, "dshSri", out var dshSri)
                    || !DshSri.TryParse(dshSri, out _))
                    return false;
                if (!element.TryGetProperty("lockfileVersion", out var lockfileVersion)
                    || lockfileVersion.ValueKind != JsonValueKind.Number
                    || lockfileVersion.GetInt32() != 3)
                    return false;
                if (!TryGetStringProperty(element, "generatedByNpm", out _))
                    return false;
                if (!element.TryGetProperty("smokePassed", out var smokePassed)
                    || smokePassed.ValueKind != JsonValueKind.True)
                    return false;
                // Entries are published newest first; any other order means
                // the file was hand-edited or truncated.
                if (parsedEntries.Count > 0 && parsedVersion.CompareTo(previousVersion) >= 0)
                    return false;
                previousVersion = parsedVersion;
                parsedEntries.Add((version, lockSha256, packageSha256, dshSri));
            }

            // A shipped PSX build always carries at least one approved
            // version; an empty catalog is a damaged installation.
            if (parsedEntries.Count == 0)
                return false;

            if (!root.TryGetProperty("blockedVersions", out var blockedElement)
                || blockedElement.ValueKind != JsonValueKind.Array
                || blockedElement.GetArrayLength() > MaxBlocked)
                return false;

            var parsedBlocked = new List<string>();
            foreach (var element in blockedElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !TryGetStringProperty(element, "version", out var version)
                    || !DshSemanticVersion.TryParse(version, out _)
                    || !TryGetStringProperty(element, "reason", out var reason)
                    || !BlockedReasons.Contains(reason))
                    return false;
                if (seenVersions.Contains(version))
                    return false;
                parsedBlocked.Add(version);
            }

            entries = parsedEntries;
            blocked = parsedBlocked;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string property, out string value)
    {
        value = "";
        if (!element.TryGetProperty(property, out var propertyElement)
            || propertyElement.ValueKind != JsonValueKind.String)
            return false;
        value = propertyElement.GetString()!;
        return value.Length > 0;
    }

    private static bool IsAtLeastSeed(DshSemanticVersion version) =>
        DshSemanticVersion.TryParse(DshWebRuntime.SeededPackageVersion, out var seed)
        && version.CompareTo(seed) >= 0;

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool TryReadVerifiedFile(
        string path, string expectedSha256, int maxBytes, out string content)
    {
        content = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > maxBytes)
                return false;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            content = File.ReadAllText(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
