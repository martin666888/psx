using System.Text;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

/// <summary>
/// Hardening tests for the bundled lock source: the catalog and artifacts
/// are read from an untrusted on-disk state, so every read is size-capped,
/// SHA-verified and path-contained before an update may trust it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DshLockSourceTests
{
    private static string Root(TestWorkspace workspace) =>
        Path.Combine(workspace.Path, "tools", "dsh-locks");

    private static void WriteCatalog(string root, string catalogJson)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.json"), catalogJson);
    }

    private static void WriteEntryFiles(string root, string version, string? lockJson = null, string? packageJson = null)
    {
        lockJson ??= FakeDshLockSource.BuildLockJson(version, DshSri.TestIntegrity);
        packageJson ??= FakeDshLockSource.BuildPackageJson(version);
        var versionDirectory = Path.Combine(root, "locks", version);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(Path.Combine(versionDirectory, "package-lock.json"), lockJson);
        File.WriteAllText(Path.Combine(versionDirectory, "package.json"), packageJson);
    }

    private static string BuildCatalogJson(
        string version,
        string? lockSha = null,
        string? packageSha = null,
        string? sri = null,
        string? extraEntries = null,
        string blocked = "[]") => $$"""
        {
          "schemaVersion": 1,
          "entries": [
            {
              "version": "{{version}}",
              "lockSha256": "{{lockSha ?? FakeDshLockSource.HashText(FakeDshLockSource.BuildLockJson(version, sri ?? DshSri.TestIntegrity))}}",
              "packageSha256": "{{packageSha ?? FakeDshLockSource.HashText(FakeDshLockSource.BuildPackageJson(version))}}",
              "lockfileVersion": 3,
              "generatedByNpm": "10.9.8",
              "dshSri": "{{sri ?? DshSri.TestIntegrity}}",
              "smokePassed": true
            }{{extraEntries}}
          ],
          "blockedVersions": {{blocked}}
        }
        """;

    [TestMethod]
    public void Find_ReturnsVerifiedArtifactForBundledVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(Find_ReturnsVerifiedArtifactForBundledVersion));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        var source = new BundledDshLockSource(root);
        var result = source.Find("0.1.0-rc.7");

        Assert.AreEqual(DshLockLookup.Found, result.Kind);
        Assert.AreEqual("0.1.0-rc.7", result.Artifact!.Version);
        Assert.AreEqual(DshLockSourceKind.Bundled, result.Artifact.Kind);
        Assert.AreEqual(DshSri.TestIntegrity, result.Artifact.DshSri);
        Assert.AreEqual(
            FakeDshLockSource.BuildPackageJson("0.1.0-rc.7"),
            result.Artifact.PackageJson);
    }

    [TestMethod]
    public void Find_MissingVersion_IsNotBundled_AndCatalogSnapshotListsVersions()
    {
        using var workspace = TestWorkspace.Create(
            nameof(Find_MissingVersion_IsNotBundled_AndCatalogSnapshotListsVersions));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7", blocked: "[{ \"version\": \"0.1.0-rc.9\", \"reason\": \"smoke_failed\" }]"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        var source = new BundledDshLockSource(root);
        Assert.AreEqual(DshLockLookup.VersionNotBundled, source.Find("0.1.0-rc.8").Kind);

        var snapshot = source.LoadCatalog();
        Assert.IsTrue(snapshot.Valid);
        CollectionAssert.AreEqual(new[] { "0.1.0-rc.7" }, snapshot.Versions.ToArray());
        CollectionAssert.AreEqual(new[] { "0.1.0-rc.9" }, snapshot.BlockedVersions.ToArray());
    }

    [TestMethod]
    public void Find_ShaMismatch_IsArtifactCorrupt()
    {
        using var workspace = TestWorkspace.Create(nameof(Find_ShaMismatch_IsArtifactCorrupt));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7", lockSha: new string('A', 64)));
        WriteEntryFiles(root, "0.1.0-rc.7");

        var result = new BundledDshLockSource(root).Find("0.1.0-rc.7");
        Assert.AreEqual(DshLockLookup.ArtifactCorrupt, result.Kind);
    }

    [TestMethod]
    public void Find_MissingArtifactFiles_IsArtifactCorrupt()
    {
        using var workspace = TestWorkspace.Create(nameof(Find_MissingArtifactFiles_IsArtifactCorrupt));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7"));
        // No locks/0.1.0-rc.7 directory at all.

        Assert.AreEqual(
            DshLockLookup.ArtifactCorrupt,
            new BundledDshLockSource(root).Find("0.1.0-rc.7").Kind);
    }

    [TestMethod]
    public void Find_OversizedArtifact_IsArtifactCorrupt()
    {
        using var workspace = TestWorkspace.Create(nameof(Find_OversizedArtifact_IsArtifactCorrupt));
        var root = Root(workspace);
        var version = "0.1.0-rc.7";
        var hugeLock = FakeDshLockSource.BuildLockJson(version, DshSri.TestIntegrity)
            + "\n" + new string('x', 5 * 1024 * 1024);
        WriteCatalog(root, BuildCatalogJson(version));
        WriteEntryFiles(root, version, lockJson: hugeLock);

        Assert.AreEqual(
            DshLockLookup.ArtifactCorrupt,
            new BundledDshLockSource(root).Find(version).Kind);
    }

    [TestMethod]
    public void Catalog_MissingOrMalformedOrWrongSchema_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(
            nameof(Catalog_MissingOrMalformedOrWrongSchema_IsCatalogInvalid));
        var root = Root(workspace);
        var source = new BundledDshLockSource(root);
        Assert.IsFalse(source.LoadCatalog().Valid);
        Assert.AreEqual(DshLockLookup.CatalogInvalid, source.Find("0.1.0-rc.7").Kind);

        WriteCatalog(root, "{ not json");
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);

        WriteCatalog(root, "{\"schemaVersion\":2,\"entries\":[],\"blockedVersions\":[]}");
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_Oversized_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(Catalog_Oversized_IsCatalogInvalid));
        var root = Root(workspace);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "catalog.json"),
            "{\"schemaVersion\":1,\"entries\":[],\"blockedVersions\":[],\"padding\":\""
                + new string('p', 200 * 1024) + "\"}");

        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_MoreThanSixteenEntries_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(
            nameof(Catalog_MoreThanSixteenEntries_IsCatalogInvalid));
        var root = Root(workspace);
        WriteCatalog(
            root,
            "{\"schemaVersion\":1,\"entries\":["
                + string.Join(",", Enumerable.Range(0, 17)
                    .Select(index =>
                        $"{{\"version\":\"0.{index}.0\",\"lockSha256\":\"x\",\"packageSha256\":\"x\",\"dshSri\":\"y\"}}"))
                + "],\"blockedVersions\":[]}");

        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_EmptyEntries_AreCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(Catalog_EmptyEntries_AreCatalogInvalid));
        var root = Root(workspace);
        WriteCatalog(
            root,
            "{ \"schemaVersion\": 1, \"entries\": [], \"blockedVersions\": [] }");

        // A shipped PSX build always carries at least one approved version;
        // an empty catalog is a damaged installation, not a valid one.
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_IncompleteEntryPolicy_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(
            nameof(Catalog_IncompleteEntryPolicy_IsCatalogInvalid));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        WriteCatalog(
            root,
            BuildCatalogJson("0.1.0-rc.7").Replace(
                "\"lockfileVersion\": 3", "\"lockfileVersion\": 2", StringComparison.Ordinal));
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "lockfileVersion must be 3");

        WriteCatalog(
            root,
            BuildCatalogJson("0.1.0-rc.7").Replace(
                "\"generatedByNpm\": \"10.9.8\",", string.Empty, StringComparison.Ordinal));
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "generatedByNpm is required");

        WriteCatalog(
            root,
            BuildCatalogJson("0.1.0-rc.7").Replace(
                "\"smokePassed\": true", "\"smokePassed\": false", StringComparison.Ordinal));
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "smokePassed must be true");
    }

    [TestMethod]
    public void Catalog_NonDescendingEntries_AreCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(Catalog_NonDescendingEntries_AreCatalogInvalid));
        var root = Root(workspace);
        var newerEntry =
            "{ \"version\": \"0.1.0-rc.9\", \"lockSha256\": \"" + new string('A', 64)
            + "\", \"packageSha256\": \"" + new string('B', 64)
            + "\", \"lockfileVersion\": 3, \"generatedByNpm\": \"10.9.8\", \"dshSri\": \""
            + DshSri.TestIntegrity + "\", \"smokePassed\": true }";
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7", extraEntries: $", {newerEntry}"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        // rc.7 followed by the newer rc.9 is ascending; entries must be
        // published newest first.
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Find_NonSemVerInput_IsNotBundled()
    {
        using var workspace = TestWorkspace.Create(nameof(Find_NonSemVerInput_IsNotBundled));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        Assert.AreEqual(
            DshLockLookup.VersionNotBundled,
            new BundledDshLockSource(root).Find("../../etc").Kind);
    }

    [TestMethod]
    public void Catalog_MissingOrNonArrayBlockedVersions_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(
            nameof(Catalog_MissingOrNonArrayBlockedVersions_IsCatalogInvalid));
        var root = Root(workspace);

        WriteCatalog(
            root,
            BuildCatalogJson("0.1.0-rc.7").Replace(
                "  \"blockedVersions\": []", string.Empty, StringComparison.Ordinal));
        WriteEntryFiles(root, "0.1.0-rc.7");
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "missing blockedVersions");

        // A JSON object is not an array: the field must not silently degrade
        // to an empty review list.
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7").Replace(
            "\"blockedVersions\": []", "\"blockedVersions\": {}", StringComparison.Ordinal));
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "object blockedVersions");
    }

    [TestMethod]
    public void Catalog_UnknownBlockedReason_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(Catalog_UnknownBlockedReason_IsCatalogInvalid));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson(
            "0.1.0-rc.7",
            blocked: "[{ \"version\": \"0.1.0-rc.9\", \"reason\": \"someone_felt_like_it\" }]"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_EntryAndBlockedOverlap_IsCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(Catalog_EntryAndBlockedOverlap_IsCatalogInvalid));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson(
            "0.1.0-rc.7",
            blocked: "[{ \"version\": \"0.1.0-rc.7\", \"reason\": \"smoke_failed\" }]"));
        WriteEntryFiles(root, "0.1.0-rc.7");

        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_DuplicateEntryVersions_AreCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(Catalog_DuplicateEntryVersions_AreCatalogInvalid));
        var root = Root(workspace);
        var sha = FakeDshLockSource.HashText(FakeDshLockSource.BuildLockJson("0.1.0-rc.7", DshSri.TestIntegrity));
        var packageSha = FakeDshLockSource.HashText(FakeDshLockSource.BuildPackageJson("0.1.0-rc.7"));
        WriteCatalog(
            root,
            $$"""
            {
              "schemaVersion": 1,
              "entries": [
                { "version": "0.1.0-rc.7", "lockSha256": "{{sha}}", "packageSha256": "{{packageSha}}", "lockfileVersion": 3, "generatedByNpm": "10.9.8", "dshSri": "{{DshSri.TestIntegrity}}", "smokePassed": true },
                { "version": "0.1.0-rc.7", "lockSha256": "{{sha}}", "packageSha256": "{{packageSha}}", "lockfileVersion": 3, "generatedByNpm": "10.9.8", "dshSri": "{{DshSri.TestIntegrity}}", "smokePassed": true }
              ],
              "blockedVersions": []
            }
            """);
        WriteEntryFiles(root, "0.1.0-rc.7");

        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid);
    }

    [TestMethod]
    public void Catalog_MalformedShaOrVersionFields_AreCatalogInvalid()
    {
        using var workspace = TestWorkspace.Create(
            nameof(Catalog_MalformedShaOrVersionFields_AreCatalogInvalid));
        var root = Root(workspace);
        WriteCatalog(root, BuildCatalogJson("0.1.0-rc.7", lockSha: new string('G', 64)));
        WriteEntryFiles(root, "0.1.0-rc.7");
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "non-hex lockSha256");

        WriteCatalog(root, BuildCatalogJson("0.1.0-not-semver"));
        WriteEntryFiles(root, "0.1.0-rc.7");
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "non-semver version");

        WriteCatalog(root, BuildCatalogJson("0.1.0-01"));
        WriteEntryFiles(root, "0.1.0-rc.7");
        Assert.IsFalse(new BundledDshLockSource(root).LoadCatalog().Valid, "leading-zero numeric prerelease");
    }
}
