using System.Globalization;
using System.Runtime.InteropServices;

namespace PSX.Helpers;

/// <summary>
/// Reads the current Windows user's preferred display language independently
/// from the process/thread UI culture that PSX changes for its own resources.
/// </summary>
internal static class WindowsUiLanguage
{
    private const uint MuiLanguageName = 0x00000008;

    public static string GetPrimaryName()
    {
        if (!OperatingSystem.IsWindows())
            return CultureInfo.InstalledUICulture.Name;

        uint languageCount = 0;
        uint characterCount = 0;
        if (!GetUserPreferredUILanguages(
                MuiLanguageName,
                out languageCount,
                null,
                ref characterCount)
            || languageCount == 0
            || characterCount < 2)
        {
            return CultureInfo.InstalledUICulture.Name;
        }

        var buffer = new char[characterCount];
        if (!GetUserPreferredUILanguages(
                MuiLanguageName,
                out languageCount,
                buffer,
                ref characterCount)
            || languageCount == 0)
        {
            return CultureInfo.InstalledUICulture.Name;
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return terminator > 0
            ? new string(buffer, 0, terminator)
            : CultureInfo.InstalledUICulture.Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(
        uint flags,
        out uint languageCount,
        [Out] char[]? languagesBuffer,
        ref uint characterCount);
}
