using System.Security.Cryptography;
using System.Text;
using PSX.Services;

namespace PSX.Tests.Support;

/// <summary>
/// In-memory <see cref="IDshLockSource"/> for DSH update tests. Bundled
/// artifacts use the same minimal lockfile shape the shared validator
/// accepts, so a staged update flows through the real validation path. The
/// corrupt sets let individual versions or the whole catalog fail the way a
/// damaged installation would.
/// </summary>
internal sealed class FakeDshLockSource : IDshLockSource
{
    private readonly Dictionary<string, DshLockArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _corruptArtifacts = new(StringComparer.Ordinal);

    public bool CatalogValid { get; set; } = true;

    /// <summary>Number of <see cref="Find"/> calls; tests assert staging
    /// failures happen without reaching npm by combining this with the
    /// scripted-npm run log.</summary>
    public int FindCalls { get; private set; }

    public void Bundle(string version, string? sri = null, string? lockJson = null)
    {
        sri ??= DshSri.TestIntegrity;
        lockJson ??= BuildLockJson(version, sri);
        var packageJson = BuildPackageJson(version);
        _artifacts[version] = new DshLockArtifact(
            version,
            packageJson,
            lockJson,
            HashText(lockJson),
            HashText(packageJson),
            sri,
            DshLockSourceKind.Bundled);
        _corruptArtifacts.Remove(version);
    }

    /// <summary>Registers the version in blockedVersions while keeping any
    /// bundled artifact absent, matching the real catalog invariant.</summary>
    public void Block(string version)
    {
        _blocked.Add(version);
        _artifacts.Remove(version);
        _corruptArtifacts.Remove(version);
    }

    /// <summary>Keeps the catalog entry but reports ArtifactCorrupt, as if
    /// the lock file on disk no longer matches its recorded SHA.</summary>
    public void Corrupt(string version) => _corruptArtifacts.Add(version);

    public DshCatalogSnapshot LoadCatalog() =>
        CatalogValid
            ? new DshCatalogSnapshot(true, _artifacts.Keys.ToArray(), _blocked.ToArray())
            : DshCatalogSnapshot.Invalid;

    public DshLockLookupResult Find(string version)
    {
        FindCalls++;
        if (!CatalogValid)
            return DshLockLookupResult.Miss(DshLockLookup.CatalogInvalid);
        if (!_artifacts.TryGetValue(version, out var artifact))
            return DshLockLookupResult.Miss(DshLockLookup.VersionNotBundled);
        if (_corruptArtifacts.Contains(version))
            return DshLockLookupResult.Miss(DshLockLookup.ArtifactCorrupt);
        return DshLockLookupResult.Found(artifact);
    }

    public static string BuildLockJson(string version, string sri) => $$"""
        {
          "name": "psx-dsh-runtime",
          "version": "0.1.0",
          "lockfileVersion": 3,
          "requires": true,
          "packages": {
            "": {
              "name": "psx-dsh-runtime",
              "version": "0.1.0",
              "dependencies": {
                "@deepseek-ai/dsh": "{{version}}"
              }
            },
            "node_modules/@deepseek-ai/dsh": {
              "version": "{{version}}",
              "resolved": "https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-{{version}}.tgz",
              "integrity": "{{sri}}"
            }
          }
        }
        """;

    public static string BuildPackageJson(string version) => $$"""
        {
          "name": "psx-dsh-runtime",
          "version": "0.1.0",
          "private": true,
          "description": "PSX-managed DeepSeek Harness runtime update.",
          "dependencies": {
            "@deepseek-ai/dsh": "{{version}}"
          }
        }
        """;

    public static string HashText(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
