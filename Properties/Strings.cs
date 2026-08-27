using System.Globalization;
using System.Resources;

namespace PSX.Properties;

/// <summary>
/// Manual accessor over the embedded resx resources (no designer
/// generation). The neutral resources are English — the canonical fallback;
/// culture-specific satellites (<c>Strings.zh-Hans.resx</c>, later zh-Hant
/// and ja) ship beside it. Reads follow the current UI culture, which the
/// app sets from the resolved locale at startup and on every locale switch.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("PSX.Properties.Strings", typeof(Strings).Assembly);

    public static string Get(string name) =>
        Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string ErrorDialogTitle => Get("ErrorDialogTitle");

    public static string StartupFailedFormat => Get("StartupFailedFormat");

    public static string InitializationFailedFormat => Get("InitializationFailedFormat");

    public static string WebView2MissingTitle => Get("WebView2MissingTitle");

    public static string WebView2MissingBody => Get("WebView2MissingBody");

    public static string LoginTabName => Get("LoginTabName");

    public static string StartupWarningWithBackup => Get("StartupWarningWithBackup");

    public static string StartupWarningSafeDefaults => Get("StartupWarningSafeDefaults");

    public static string StartupWarningBackupAlsoInvalid => Get("StartupWarningBackupAlsoInvalid");

    public static string ZipArchiveFilter => Get("ZipArchiveFilter");
    public static string DismissWarningToolTip => Get("DismissWarningToolTip");
    public static string ThemeStatusInvalid => Get("ThemeStatusInvalid");
    public static string ThemeStatusUpdated => Get("ThemeStatusUpdated");
    public static string ThemeStatusCurrent => Get("ThemeStatusCurrent");
    public static string DshWorkspaceLabel => Get("DshWorkspaceLabel");
    public static string KimiWorkspaceLabel => Get("KimiWorkspaceLabel");
    public static string RuntimeOperationExitTitle => Get("RuntimeOperationExitTitle");
    public static string RuntimeOperationExitBody => Get("RuntimeOperationExitBody");

    public static string TabNewToRightColumn => Get("TabNewToRightColumn");

    public static string TabMoveToNewColumn => Get("TabMoveToNewColumn");

    public static string TabMergeIntoSingleColumn => Get("TabMergeIntoSingleColumn");
}
