using System.Globalization;

namespace PSX.Models;

/// <summary>
/// Fixed UI locale modes and their resolution to concrete display languages.
/// <c>localeMode</c> is the persisted preference (five wire values);
/// <c>resolvedLocale</c> is always one of the four shipped languages and is
/// what the WebView bootstrap, WPF culture and Intl formatting consume.
/// </summary>
public static class LocaleDescriptor
{
    public const string System = "system";
    public const string ZhHans = "zh-Hans";
    public const string ZhHant = "zh-Hant";
    public const string En = "en";
    public const string Ja = "ja";

    /// <summary>Upgraded installs keep today's Chinese UI even on a non-Chinese
    /// Windows; fresh installs follow Windows.</summary>
    public const string UpgradeDefaultMode = ZhHans;
    public const string FreshInstallDefaultMode = System;

    public static bool IsMode(string? value) =>
        value is System or ZhHans or ZhHant or En or Ja;

    /// <summary>Resolve a stored mode to a display language. Invalid values
    /// fall back to following Windows, which itself falls back to English.</summary>
    public static string Resolve(string? mode)
    {
        if (mode is ZhHans or ZhHant or En or Ja)
            return mode;
        return ResolveSystem();
    }

    /// <summary>Map the Windows UI culture: zh-TW/HK/MO (and zh-Hant) are
    /// Traditional; other zh regions are Simplified; ja maps to Japanese;
    /// everything unsupported falls back to English.</summary>
    public static string ResolveSystem()
    {
        for (var culture = CultureInfo.CurrentUICulture;
             culture != null && !string.IsNullOrEmpty(culture.Name);
             culture = culture.Parent)
        {
            var name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return name is "zh-TW" or "zh-HK" or "zh-MO" or "zh-Hant"
                    ? ZhHant
                    : ZhHans;
            }
            if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                return Ja;
            if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return En;
        }

        return En;
    }
}
