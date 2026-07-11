using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

public interface IThemeService
{
    string UserThemeDirectory { get; }
    IReadOnlyList<ThemeDescriptor> ScanThemes();
    ThemeValidationResult LoadTheme(string filePath, ThemeSource source);
    string ComputeFingerprint(AppearanceSettings appearance);
    void OpenUserThemeDirectory();
}

public sealed partial class ThemeService : IThemeService
{
    private static readonly string[] RequiredSections = ["meta", "terminal", "agent", "theme", "agentTheme", "terminalColors"];
    private readonly string _builtInDirectory = Path.Combine(AppContext.BaseDirectory, "theme-presets");

    public string UserThemeDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".psx", "themes");

    public IReadOnlyList<ThemeDescriptor> ScanThemes()
    {
        Directory.CreateDirectory(UserThemeDirectory);
        var themes = new List<ThemeDescriptor>();
        ScanDirectory(_builtInDirectory, ThemeSource.BuiltIn, themes);
        ScanDirectory(UserThemeDirectory, ThemeSource.User, themes);

        foreach (var group in themes
                     .Where(t => t.Availability == ThemeAvailability.Available)
                     .GroupBy(t => (t.Source, t.Id), new ThemeIdentityComparer())
                     .Where(g => g.Count() > 1))
        {
            foreach (var theme in group)
            {
                theme.Availability = ThemeAvailability.Invalid;
                theme.Diagnostics.Add(new ThemeDiagnostic
                {
                    IsError = true,
                    Message = $"Duplicate theme id '{theme.Id}' in {theme.Source} themes."
                });
            }
        }

        return themes
            .OrderBy(t => t.Source)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ThemeValidationResult LoadTheme(string filePath, ThemeSource source)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(filePath);
        try
        {
            var document = IniDocument.Parse(
                File.ReadAllLines(filePath, new UTF8Encoding(false, true)),
                Path.GetFileName(filePath));
            foreach (var section in RequiredSections)
            {
                if (!document.Sections.ContainsKey(section))
                    throw new InvalidDataException($"Missing required section [{section}].");
            }

            var kind = Require(document, "meta", "kind");
            if (!string.Equals(kind, "theme", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("[meta].kind must be 'theme'.");

            if (!int.TryParse(Require(document, "meta", "schemaVersion"), out var version) || version != 1)
                throw new InvalidDataException("[meta].schemaVersion must be 1.");

            var id = Require(document, "meta", "id").ToLowerInvariant();
            if (!ThemeIdRegex().IsMatch(id))
                throw new InvalidDataException("[meta].id must use 1-64 lowercase letters, digits, '.', '_' or '-'.");

            var name = Require(document, "meta", "name");
            var author = document.Get("meta", "author") ?? "";
            var appearance = new AppearanceSettings
            {
                TerminalFontSize = RequireFontSize(document, "terminal"),
                TerminalFontFamily = RequireText(document, "terminal", "fontFamily"),
                AgentFontSize = RequireFontSize(document, "agent"),
                AgentFontFamily = RequireText(document, "agent", "fontFamily"),
                AgentMonoFontFamily = RequireText(document, "agent", "monoFontFamily")
            };

            PopulateColors(document, "theme", appearance.ThemeColors);
            PopulateColors(document, "agentTheme", appearance.AgentTheme);
            PopulateColors(document, "terminalColors", appearance.TerminalColors);

            var keyPrefix = source == ThemeSource.BuiltIn ? "builtin" : "user";
            var descriptor = new ThemeDescriptor
            {
                Key = $"{keyPrefix}:{id}",
                Id = id,
                Name = name,
                Author = author,
                FilePath = Path.GetFullPath(filePath),
                Source = source,
                Availability = ThemeAvailability.Available,
                Appearance = appearance,
                Fingerprint = ComputeFingerprint(appearance)
            };

            AddUnknownWarnings(document, descriptor);
            AddContrastWarning(descriptor);
            return new ThemeValidationResult { Descriptor = descriptor };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
        {
            var descriptor = new ThemeDescriptor
            {
                Name = fallbackName,
                FilePath = Path.GetFullPath(filePath),
                Source = source,
                Availability = ThemeAvailability.Invalid
            };
            descriptor.Diagnostics.Add(new ThemeDiagnostic { IsError = true, Message = ex.Message });
            return new ThemeValidationResult { Descriptor = descriptor };
        }
    }

    public string ComputeFingerprint(AppearanceSettings appearance)
    {
        var builder = new StringBuilder();
        builder.AppendLine(appearance.TerminalFontSize.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(appearance.TerminalFontFamily.Trim());
        builder.AppendLine(appearance.AgentFontSize.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(appearance.AgentFontFamily.Trim());
        builder.AppendLine(appearance.AgentMonoFontFamily.Trim());
        AppendProperties(builder, appearance.ThemeColors);
        AppendProperties(builder, appearance.AgentTheme);
        AppendProperties(builder, appearance.TerminalColors);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public void OpenUserThemeDirectory()
    {
        Directory.CreateDirectory(UserThemeDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = UserThemeDirectory,
            UseShellExecute = true
        });
    }

    private void ScanDirectory(string directory, ThemeSource source, List<ThemeDescriptor> destination)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.ini", SearchOption.TopDirectoryOnly))
            destination.Add(LoadTheme(path, source).Descriptor);
    }

    private static string Require(IniDocument document, string section, string key)
    {
        var value = document.Get(section, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Missing required field [{section}].{key}.");
        return value.Trim();
    }

    private static string RequireText(IniDocument document, string section, string key)
    {
        var value = Require(document, section, key);
        if (value.Length > 512)
            throw new InvalidDataException($"[{section}].{key} is too long.");
        return value;
    }

    private static int RequireFontSize(IniDocument document, string section)
    {
        var raw = Require(document, section, "fontSize");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is < 6 or > 72)
            throw new InvalidDataException($"[{section}].fontSize must be between 6 and 72.");
        return value;
    }

    private static void PopulateColors<T>(IniDocument document, string section, T target)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var key = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            var value = Require(document, section, key);
            if (!CssColorRegex().IsMatch(value))
                throw new InvalidDataException($"[{section}].{key} must be #RRGGBB or #RRGGBBAA.");
            property.SetValue(target, value.ToLowerInvariant());
        }
    }

    private static void AppendProperties<T>(StringBuilder builder, T source)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.CanRead)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
            builder.AppendLine($"{property.Name.ToLowerInvariant()}={property.GetValue(source)?.ToString()?.Trim().ToLowerInvariant()}");
    }

    private static void AddUnknownWarnings(IniDocument document, ThemeDescriptor descriptor)
    {
        var known = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["meta"] = new(["kind", "schemaVersion", "id", "name", "author"], StringComparer.OrdinalIgnoreCase),
            ["terminal"] = new(["fontSize", "fontFamily"], StringComparer.OrdinalIgnoreCase),
            ["agent"] = new(["fontSize", "fontFamily", "monoFontFamily"], StringComparer.OrdinalIgnoreCase),
            ["theme"] = PropertyKeys<ThemeColors>(),
            ["agentTheme"] = PropertyKeys<AgentThemeColors>(),
            ["terminalColors"] = PropertyKeys<TerminalPalette>()
        };

        foreach (var section in document.Sections)
        {
            if (!known.TryGetValue(section.Key, out var keys))
            {
                descriptor.Diagnostics.Add(new ThemeDiagnostic { Message = $"Unknown section [{section.Key}] is ignored." });
                continue;
            }
            foreach (var key in section.Value.Keys.Where(k => !keys.Contains(k)))
                descriptor.Diagnostics.Add(new ThemeDiagnostic { Message = $"Unknown field [{section.Key}].{key} is ignored." });
        }
    }

    private static HashSet<string> PropertyKeys<T>() => new(
        typeof(T).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]),
        StringComparer.OrdinalIgnoreCase);

    private static void AddContrastWarning(ThemeDescriptor descriptor)
    {
        var appearance = descriptor.Appearance;
        if (appearance == null || ContrastRatio(appearance.ThemeColors.Background, appearance.ThemeColors.Text) >= 4.5)
            return;
        descriptor.Diagnostics.Add(new ThemeDiagnostic
        {
            Message = "Background and primary text contrast is below 4.5:1."
        });
    }

    private static double ContrastRatio(string first, string second)
    {
        static double Luminance(string value)
        {
            var channels = new[] { value[1..3], value[3..5], value[5..7] }
                .Select(v => int.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d)
                .Select(v => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4))
                .ToArray();
            return (0.2126 * channels[0]) + (0.7152 * channels[1]) + (0.0722 * channels[2]);
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private sealed class ThemeIdentityComparer : IEqualityComparer<(ThemeSource Source, string Id)>
    {
        public bool Equals((ThemeSource Source, string Id) x, (ThemeSource Source, string Id) y) =>
            x.Source == y.Source && string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ThemeSource Source, string Id) obj) =>
            HashCode.Combine(obj.Source, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeIdRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CssColorRegex();
}
