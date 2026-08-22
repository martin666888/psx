namespace PSX.Services;

/// <summary>
/// Fixed DSH npm registry presets. Unknown keys never become a live origin;
/// the bridge parser rejects them, and disk reads fall back through
/// <see cref="ResolveStoredOrOfficial"/>.
/// </summary>
public sealed record DshRegistryDescriptor(string Key, string Origin, string Label)
{
    public const string OfficialKey = "official";
    public const string NpmmirrorKey = "npmmirror";

    public const string OfficialOrigin = "https://registry.npmjs.org/";
    public const string NpmmirrorOrigin = "https://registry.npmmirror.com/";

    public static DshRegistryDescriptor Official { get; } =
        new(OfficialKey, OfficialOrigin, "npm 官方");

    public static DshRegistryDescriptor Npmmirror { get; } =
        new(NpmmirrorKey, NpmmirrorOrigin, "npmmirror");

    public bool IsOfficial => string.Equals(Key, OfficialKey, StringComparison.Ordinal);
    public bool IsNpmmirror => string.Equals(Key, NpmmirrorKey, StringComparison.Ordinal);

    public static bool TryGet(string? key, out DshRegistryDescriptor descriptor)
    {
        if (string.Equals(key, OfficialKey, StringComparison.Ordinal))
        {
            descriptor = Official;
            return true;
        }

        if (string.Equals(key, NpmmirrorKey, StringComparison.Ordinal))
        {
            descriptor = Npmmirror;
            return true;
        }

        descriptor = Official;
        return false;
    }

    /// <summary>Disk / in-memory preference only. Illegal values become official.</summary>
    public static DshRegistryDescriptor ResolveStoredOrOfficial(string? key) =>
        TryGet(key, out var descriptor) ? descriptor : Official;

    public bool MatchesResolvedUrl(string? value) => DshSri.IsTrustedRegistryUrl(value, Origin);
}

/// <summary>Fixed wire error classes for DSH runtime/update failures.</summary>
public static class DshErrorClass
{
    public const string RegistryTimeout = "registry_timeout";
    public const string RegistryNetwork = "registry_network";
    public const string RegistryNotFound = "registry_not_found";
    public const string IntegrityFailed = "integrity_failed";
    public const string InstallFailed = "install_failed";
    public const string LaunchFailed = "launch_failed";
    public const string SettingsWriteFailed = "settings_write_failed";
    public const string LockUnavailable = "lock_unavailable";
    public const string CatalogCorrupt = "catalog_corrupt";

    public static bool IsKnown(string? value) =>
        value is RegistryTimeout or RegistryNetwork or RegistryNotFound
            or IntegrityFailed or InstallFailed or LaunchFailed
            or SettingsWriteFailed or LockUnavailable or CatalogCorrupt;
}

/// <summary>
/// Strict sha512 SRI helpers. A passing value is one <c>sha512-</c> token
/// whose payload decodes to exactly 64 bytes. This is format validation plus
/// equality against a chosen source — not an independent official signature.
/// </summary>
public static class DshSri
{
    public const string Prefix = "sha512-";

    /// <summary>Deterministic 64-zero digest used by tests and fake npm scripts.</summary>
    public static readonly string TestIntegrity =
        Prefix + Convert.ToBase64String(new byte[64]);

    public static bool TryParse(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
            return false;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var payload = value[Prefix.Length..];
        if (payload.Length == 0)
            return false;
        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length != 64)
                return false;
        }
        catch (FormatException)
        {
            return false;
        }

        normalized = Prefix + payload;
        return true;
    }

    public static bool IsValid(string? value) => TryParse(value, out _);

    public static bool Equal(string? left, string? right) =>
        TryParse(left, out var a)
        && TryParse(right, out var b)
        && string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// HTTPS, exact host, default port, no userinfo. Rejects substrings,
    /// subdomains and prefix tricks.
    /// </summary>
    public static bool IsTrustedRegistryUrl(string? value, string origin)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var expected))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (!uri.IsDefaultPort)
            return false;
        return string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase);
    }
}
