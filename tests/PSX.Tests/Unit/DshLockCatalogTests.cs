using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

/// <summary>
/// Repository-side gate for tools/dsh-locks/: every committed catalog entry
/// must carry its exact lock artifacts and pass the same validator the PSX
/// client uses at update time. The catalog must carry at least one entry —
/// an empty catalog defers every update and cannot ship.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class DshLockCatalogTests
{
    private static readonly string LocksRoot =
        Path.Combine(TestWorkspace.RepositoryRoot, "tools", "dsh-locks");

    private static readonly string[] BlockedReasons =
        ["smoke_failed", "official_integrity_mismatch", "sri_conflict"];

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex Sha256HexPattern();

    [TestMethod]
    public void Catalog_SchemaAndPolicyIsValid()
    {
        using var catalog = ReadCatalog(out var root);
        AssertCatalogEntryPolicy(root);
        AssertBlockedVersionPolicy(root);
    }

    [TestMethod]
    public void Catalog_Entries_MatchCommittedLockArtifacts()
    {
        using var catalog = ReadCatalog(out var root);
        var entries = (root.TryGetProperty("entries", out var entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
            ? entriesElement.EnumerateArray().ToArray()
            : [];

        foreach (var entry in entries)
        {
            var version = entry.GetProperty("version").GetString()!;
            var versionDirectory = Path.Combine(LocksRoot, "locks", version);
            var lockPath = Path.Combine(versionDirectory, "package-lock.json");
            var packagePath = Path.Combine(versionDirectory, "package.json");
            Assert.IsTrue(File.Exists(lockPath), $"{version}: package-lock.json is missing");
            Assert.IsTrue(File.Exists(packagePath), $"{version}: package.json is missing");

            Assert.AreEqual(
                entry.GetProperty("lockSha256").GetString(),
                HashFile(lockPath),
                ignoreCase: true,
                $"{version}: committed lock SHA does not match the catalog");
            Assert.AreEqual(
                entry.GetProperty("packageSha256").GetString(),
                HashFile(packagePath),
                ignoreCase: true,
                $"{version}: committed package.json SHA does not match the catalog");

            var dshSri = entry.GetProperty("dshSri").GetString()!;
            Assert.IsNull(
                DshLockValidator.ValidateLockFile(
                    versionDirectory, version, DshRegistryDescriptor.OfficialOrigin, dshSri),
                $"{version}: committed lock failed the shared validator");
            Assert.IsTrue(
                DshSri.Equal(
                    DshLockValidator.ReadRootPackageIntegrity(versionDirectory, version), dshSri),
                $"{version}: catalog dshSri does not equal the lock's DSH entry SRI");

            using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
            Assert.IsTrue(
                package.RootElement.TryGetProperty("dependencies", out var dependencies)
                    && dependencies.TryGetProperty(DshWebRuntime.DshPackageName, out var pinned)
                    && pinned.ValueKind == JsonValueKind.String
                    && string.Equals(pinned.GetString(), version, StringComparison.Ordinal),
                $"{version}: root package.json does not pin the exact DSH dependency");
        }
    }

    [TestMethod]
    public void FirstInstallSeed_MatchesReviewedCatalogArtifact()
    {
        var release = File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "tools", "build-release.ps1"));
        var pin = Regex.Match(release, "\\$DshPinnedVersion = \"([^\"]+)\"").Groups[1].Value;
        Assert.AreEqual(DshWebRuntime.SeededPackageVersion, pin);
        var seedDirectory = Path.Combine(TestWorkspace.RepositoryRoot, "tools", "dsh-seed");
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(seedDirectory, "package.json")));
        Assert.AreEqual(pin, seed.RootElement.GetProperty("dependencies").GetProperty(DshWebRuntime.DshPackageName).GetString());
        var artifactDirectory = Path.Combine(LocksRoot, "locks", DshWebRuntime.SeededPackageVersion);
        foreach (var file in new[] { "package.json", "package-lock.json" })
        {
            Assert.AreEqual(HashFile(Path.Combine(artifactDirectory, file)),
                HashFile(Path.Combine(seedDirectory, file)), $"First-install {file} must match its reviewed artifact");
        }
    }

    [TestMethod]
    public void Catalog_LocksDirectory_HasNoOrphanDirectories()
    {
        using var catalog = ReadCatalog(out var root);
        var versionsDirectory = Path.Combine(LocksRoot, "locks");
        var onDisk = Directory.Exists(versionsDirectory)
            ? Directory.GetDirectories(versionsDirectory)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToArray()
            : [];

        var expected = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
                expected.Add(entry.GetProperty("version").GetString()!);
        }

        foreach (var orphan in onDisk.Where(dir => !expected.Contains(dir)))
            Assert.Fail($"locks/ contains a directory not listed in the catalog: {orphan}");
    }

    [TestMethod]
    public void HashedSupplyChainEvidence_UsesLfOnly()
    {
        var paths = new List<string>
        {
            Path.Combine(TestWorkspace.RepositoryRoot, "tools", "dsh-seed", "package.json"),
            Path.Combine(TestWorkspace.RepositoryRoot, "tools", "dsh-seed", "package-lock.json"),
            Path.Combine(LocksRoot, "catalog.json")
        };
        var versionsDirectory = Path.Combine(LocksRoot, "locks");
        if (Directory.Exists(versionsDirectory))
        {
            paths.AddRange(Directory.GetFiles(versionsDirectory, "*.json", SearchOption.AllDirectories));
        }

        foreach (var path in paths)
        {
            Assert.IsTrue(File.Exists(path), $"hashed DSH evidence is missing: {path}");
            Assert.IsFalse(
                File.ReadAllBytes(path).Contains((byte)'\r'),
                $"hashed DSH evidence must use deterministic LF line endings: {path}");
        }
    }

    private static JsonDocument ReadCatalog(out JsonElement root)
    {
        var catalogPath = Path.Combine(LocksRoot, "catalog.json");
        Assert.IsTrue(File.Exists(catalogPath), "tools/dsh-locks/catalog.json is missing");
        var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        root = document.RootElement;
        return document;
    }

    private static void AssertCatalogEntryPolicy(JsonElement root)
    {
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32(), "catalog schemaVersion");
        Assert.IsTrue(
            root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array,
            "catalog entries must be an array");

        var versions = new List<(DshSemanticVersion Parsed, string Version)>();
        foreach (var entry in entries.EnumerateArray())
        {
            var version = AssertStringProperty(entry, "version", $"entry");
            Assert.IsTrue(
                DshSemanticVersion.TryParse(version, out var parsed),
                $"{version}: entry version is not valid SemVer 2");
            Assert.IsTrue(
                DshSemanticVersion.TryParse(DshWebRuntime.MinimumSupportedPackageVersion, out var seed)
                    && parsed.CompareTo(seed) >= 0,
                $"{version}: entry is older than the release seed");

            Assert.IsTrue(
                Sha256HexPattern().IsMatch(AssertStringProperty(entry, "lockSha256", version)),
                $"{version}: lockSha256 is not 64 hex characters");
            Assert.IsTrue(
                Sha256HexPattern().IsMatch(AssertStringProperty(entry, "packageSha256", version)),
                $"{version}: packageSha256 is not 64 hex characters");
            Assert.AreEqual(3, entry.GetProperty("lockfileVersion").GetInt32(), $"{version}: lockfileVersion");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(AssertStringProperty(entry, "generatedByNpm", version)),
                $"{version}: generatedByNpm is empty");
            Assert.IsTrue(
                DshSri.TryParse(AssertStringProperty(entry, "dshSri", version), out _),
                $"{version}: dshSri is not a sha512 SRI");
            Assert.IsTrue(
                entry.GetProperty("smokePassed").GetBoolean(), $"{version}: smokePassed");
            versions.Add((parsed, version));
        }

        // Release contract: every PSX build ships one to five approved,
        // smoke-verified lock entries. An empty catalog makes every newer
        // version deferred and fails the release freshness gate, so the
        // repository gate refuses it too.
        Assert.IsInRange(1, 5, versions.Count, "catalog carries between one and five approved entries (-Keep)");
        for (var index = 1; index < versions.Count; index++)
            Assert.IsGreaterThan(
                0,
                versions[index - 1].Parsed.CompareTo(versions[index].Parsed),
                $"entries must be sorted newest first: {versions[index - 1].Version} then {versions[index].Version}");
    }

    private static void AssertBlockedVersionPolicy(JsonElement root)
    {
        Assert.IsTrue(
            root.TryGetProperty("blockedVersions", out var blocked)
            && blocked.ValueKind == JsonValueKind.Array,
            "catalog blockedVersions must be an array");
        Assert.IsLessThanOrEqualTo(10, blocked.GetArrayLength(), "blockedVersions keeps at most ten entries");

        var entryVersions = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
                entryVersions.Add(entry.GetProperty("version").GetString()!);
        }

        foreach (var blockedEntry in blocked.EnumerateArray())
        {
            var version = AssertStringProperty(blockedEntry, "version", "blocked entry");
            Assert.IsTrue(
                DshSemanticVersion.TryParse(version, out _),
                $"{version}: blocked version is not valid SemVer 2");
            var reason = AssertStringProperty(blockedEntry, "reason", version);
            Assert.IsTrue(
                BlockedReasons.Contains(reason),
                $"{version}: blocked reason '{reason}' is not a fixed enum key");
            Assert.DoesNotContain(
                version, entryVersions,
                $"{version}: a version cannot be both bundled and blocked");
        }
    }

    private static string AssertStringProperty(JsonElement element, string property, string context)
    {
        Assert.IsTrue(
            element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()),
            $"{context}: '{property}' must be a non-empty string");
        return value.GetString()!;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
