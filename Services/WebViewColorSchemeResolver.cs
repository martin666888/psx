using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace PSX.Services;

/// <summary>
/// Resolves the browser preference advertised to embedded web applications
/// from the active PSX canvas color. This keeps <c>prefers-color-scheme</c>
/// aligned with the selected PSX theme instead of the Windows theme.
/// </summary>
internal static class WebViewColorSchemeResolver
{
    public static CoreWebView2PreferredColorScheme Resolve(string? background)
    {
        if (string.IsNullOrWhiteSpace(background))
            return CoreWebView2PreferredColorScheme.Auto;

        var value = background.Trim();
        if (value.Length is not (7 or 9) || value[0] != '#')
            return CoreWebView2PreferredColorScheme.Auto;

        if (!byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return CoreWebView2PreferredColorScheme.Auto;
        }

        var luminance = ((0.2126 * red) + (0.7152 * green) + (0.0722 * blue)) / 255;
        return luminance < 0.5
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;
    }
}
