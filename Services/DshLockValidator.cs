using System.IO;
using System.Text.Json;

namespace PSX.Services;

/// <summary>
/// Shared structural gate for DSH lockfiles: the root dependency must pin
/// the exact DSH version, every package entry must carry a trusted-origin
/// <c>resolved</c> URL and a sha512 SRI, and the DSH entry must match the
/// expected root SRI when one is provided. Used by the update stage path,
/// the startup launch gate and the repository-side lock-catalog tests, so
/// generated artifacts and client validation can never drift apart.
/// </summary>
internal static class DshLockValidator
{
    /// <summary>Returns null when the lockfile is acceptable; otherwise a
    /// fixed diagnostic string that never crosses the bridge.</summary>
    internal static string? ValidateLockFile(
        string directory,
        string expectedVersion,
        string origin,
        string? expectedRootSri)
    {
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
                || !dependencies.TryGetProperty(DshWebRuntime.DshPackageName, out var requested)
                || requested.ValueKind != JsonValueKind.String
                || !string.Equals(requested.GetString(), expectedVersion, StringComparison.Ordinal))
                return "lockfile root dependency is not exact";

            var dshLockKey = "node_modules/@deepseek-ai/dsh";
            if (!packages.TryGetProperty(dshLockKey, out var dshPackage)
                || !PackageEntryMatches(dshPackage, expectedVersion, origin, expectedRootSri))
                return "DSH lock entry is not registry/integrity pinned";

            foreach (var entry in packages.EnumerateObject())
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                var value = entry.Value;
                if (value.TryGetProperty("link", out var link)
                    && link.ValueKind == JsonValueKind.True)
                    continue;
                if (value.TryGetProperty("inBundle", out var inBundle)
                    && inBundle.ValueKind == JsonValueKind.True)
                    continue;
                if (!value.TryGetProperty("resolved", out var resolved)
                    || resolved.ValueKind != JsonValueKind.String
                    || !DshSri.IsTrustedRegistryUrl(resolved.GetString(), origin)
                    || !value.TryGetProperty("integrity", out var integrity)
                    || integrity.ValueKind != JsonValueKind.String
                    || !DshSri.IsValid(integrity.GetString()))
                    return $"lock entry '{entry.Name}' is not registry/integrity pinned";
            }
        }
        catch (Exception ex)
        {
            return $"lockfile could not be parsed: {ex.GetType().Name}";
        }

        return null;
    }

    private static bool PackageEntryMatches(
        JsonElement entry,
        string expectedVersion,
        string origin,
        string? expectedRootSri)
    {
        if (!entry.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), expectedVersion, StringComparison.Ordinal)
            || !entry.TryGetProperty("resolved", out var resolved)
            || resolved.ValueKind != JsonValueKind.String
            || !DshSri.IsTrustedRegistryUrl(resolved.GetString(), origin)
            || !entry.TryGetProperty("integrity", out var integrity)
            || integrity.ValueKind != JsonValueKind.String
            || !DshSri.TryParse(integrity.GetString(), out var sri))
            return false;
        return expectedRootSri == null || DshSri.Equal(sri, expectedRootSri);
    }

    /// <summary>Reads the sha512 SRI pinned for the DSH package entry in the
    /// lockfile, or null when it cannot be read.</summary>
    internal static string? ReadRootPackageIntegrity(string directory, string expectedVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "package-lock.json")));
            if (!document.RootElement.TryGetProperty("packages", out var packages)
                || !packages.TryGetProperty("node_modules/@deepseek-ai/dsh", out var dshPackage)
                || !dshPackage.TryGetProperty("version", out var version)
                || !string.Equals(version.GetString(), expectedVersion, StringComparison.Ordinal)
                || !dshPackage.TryGetProperty("integrity", out var integrity)
                || !DshSri.TryParse(integrity.GetString(), out var sri))
                return null;
            return sri;
        }
        catch
        {
            return null;
        }
    }
}
