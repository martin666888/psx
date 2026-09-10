using System.Text.Json.Serialization;

namespace PSX.Models;

public sealed class AppSettings
{
    public const string AgentUiFontFamily = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Segoe UI Emoji, sans-serif";
    public const string BundledAgentMonoFontFamily = "PSX Maple Mono, Segoe UI Emoji, Microsoft YaHei UI, monospace";

    public string ActiveThemeKey { get; set; } = "";
    public string ThemeFingerprint { get; set; } = "";
    public string DefaultShellProfileId { get; set; } = "powershell";
    public int FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Cascadia Code, Consolas, monospace";
    public string Theme { get; set; } = "dark";
    public int Scrollback { get; set; } = 10000;
    public int AgentFontSize { get; set; } = 15;
    public string AgentFontFamily { get; set; } = AgentUiFontFamily;
    public string AgentMonoFontFamily { get; set; } = BundledAgentMonoFontFamily;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public double WindowX { get; set; }
    public double WindowY { get; set; }

    public ThemeColors ThemeColors { get; set; } = new();
    public AgentThemeColors AgentTheme { get; set; } = new();
    public TerminalPalette TerminalColors { get; set; } = new();
    public ShellThemeColors ShellTheme { get; set; } = new();
}

public sealed class AppearanceSettings
{
    public int TerminalFontSize { get; set; }
    public string TerminalFontFamily { get; set; } = "";
    public int AgentFontSize { get; set; }
    public string AgentFontFamily { get; set; } = "";
    public string AgentMonoFontFamily { get; set; } = "";
    public ThemeColors ThemeColors { get; set; } = new();
    public AgentThemeColors AgentTheme { get; set; } = new();
    public TerminalPalette TerminalColors { get; set; } = new();
    public ShellThemeColors ShellTheme { get; set; } = new();

    public static AppearanceSettings FromSettings(AppSettings settings) => new()
    {
        TerminalFontSize = settings.FontSize,
        TerminalFontFamily = settings.FontFamily,
        AgentFontSize = settings.AgentFontSize,
        AgentFontFamily = settings.AgentFontFamily,
        AgentMonoFontFamily = settings.AgentMonoFontFamily,
        ThemeColors = Clone(settings.ThemeColors),
        AgentTheme = Clone(settings.AgentTheme),
        TerminalColors = Clone(settings.TerminalColors),
        ShellTheme = Clone(settings.ShellTheme)
    };

    public void ApplyTo(AppSettings settings)
    {
        settings.FontSize = TerminalFontSize;
        settings.FontFamily = TerminalFontFamily;
        settings.AgentFontSize = AgentFontSize;
        settings.AgentFontFamily = AgentFontFamily;
        settings.AgentMonoFontFamily = AgentMonoFontFamily;
        settings.ThemeColors = Clone(ThemeColors);
        settings.AgentTheme = Clone(AgentTheme);
        settings.TerminalColors = Clone(TerminalColors);
        settings.ShellTheme = Clone(ShellTheme);
    }

    public AppearanceSettings Clone()
    {
        var settings = new AppSettings();
        ApplyTo(settings);
        return FromSettings(settings);
    }

    private static T Clone<T>(T source) where T : new()
    {
        var clone = new T();
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.SetValue(clone, property.GetValue(source));
        return clone;
    }
}

// Defaults align with the bundled base-light preset so a fresh install
// (no psx.ini) renders the light shell; loading an existing psx.ini always
// overrides these values.
public sealed class ThemeColors
{
    public string Background { get; set; } = "#ffffff";
    public string Surface { get; set; } = "#f7f7fa";
    public string SurfaceRaised { get; set; } = "#ffffff";
    public string SurfaceMuted { get; set; } = "#e8ebf0";
    public string Hover { get; set; } = "#e8ebf0";
    public string Border { get; set; } = "#dee0e5";
    public string BorderStrong { get; set; } = "#c6c8d0";
    public string Text { get; set; } = "#26292e";
    public string TextMuted { get; set; } = "#6e737a";
    public string TextDim { get; set; } = "#9aa0a8";
    public string Accent { get; set; } = "#171717";
    public string AccentHover { get; set; } = "#000000";
    public string Error { get; set; } = "#e7000b";
    public string ErrorBg { get; set; } = "#fdecec";
    public string Warning { get; set; } = "#a35200";
    public string WarningBg { get; set; } = "#fff4e5";
    public string Scrollbar { get; set; } = "#d4d4d4";
    public string ScrollbarHover { get; set; } = "#a3a3a3";
}

public sealed class AgentThemeColors
{
    public string CodeBlockBg { get; set; } = "#f7f7fa";
    public string CodeBlockText { get; set; } = "#26292e";
    public string CodeBlockBorder { get; set; } = "#dee0e5";
    public string SendBtn { get; set; } = "#26292e";
    public string SendBtnHover { get; set; } = "#1a1d22";
    public string SendBtnText { get; set; } = "#ffffff";
    public string DecisionPrimary { get; set; } = "#26292e";
    public string DecisionPrimaryHover { get; set; } = "#1a1d22";
    public string DecisionPrimaryText { get; set; } = "#ffffff";
    public string StopBtn { get; set; } = "#b23b3b";
    public string StopBtnHover { get; set; } = "#9c3333";
    public string AllowColor { get; set; } = "#297a3a";
    public string DenyColor { get; set; } = "#e7000b";
    public string CautionColor { get; set; } = "#a35200";
    public string PermissionBg { get; set; } = "#f7f7fa";
    public string PermissionBorder { get; set; } = "#c6c8d0";
    public string ElicitationBg { get; set; } = "#f4faf9";
    public string ElicitationBorder { get; set; } = "#cfe5e1";
    public string Overlay { get; set; } = "#000000";
    public string Shadow { get; set; } = "#000000";
    public string FocusRing { get; set; } = "#1f7a72";
    // Optional workbench backdrop tint painted as a top-down gradient behind
    // the two Agent panels. RRGGBBAA; fully transparent means "no tint".
    public string WorkbenchTint { get; set; } = "#00000000";
}

public sealed class TerminalPalette
{
    public string Foreground { get; set; } = "#171717";
    public string Cursor { get; set; } = "#171717";
    public string CursorAccent { get; set; } = "#ffffff";
    public string SelectionBackground { get; set; } = "#b3d7ff88";
    public string SelectionForeground { get; set; } = "#171717";
    public string Black { get; set; } = "#000000";
    public string Red { get; set; } = "#e7000b";
    public string Green { get; set; } = "#297a3a";
    public string Yellow { get; set; } = "#a35200";
    public string Blue { get; set; } = "#0068d6";
    public string Magenta { get; set; } = "#7d00cc";
    public string Cyan { get; set; } = "#0087a8";
    public string White { get; set; } = "#d4d4d4";
    public string BrightBlack { get; set; } = "#525252";
    public string BrightRed { get; set; } = "#ff6467";
    public string BrightGreen { get; set; } = "#3fa354";
    public string BrightYellow { get; set; } = "#d17d00";
    public string BrightBlue { get; set; } = "#52a8ff";
    public string BrightMagenta { get; set; } = "#bf7af0";
    public string BrightCyan { get; set; } = "#00b3d6";
    public string BrightWhite { get; set; } = "#fafafa";
}

/// <summary>
/// Optional shell chrome colors (Agent shell sidebar, tab strip, composer).
/// Every field is nullable: unset fields never enter required-key validation
/// and the frontend keeps its previous value for them.
/// </summary>
public sealed class ShellThemeColors
{
    [JsonPropertyName("sidebar")]
    public string? Sidebar { get; set; }

    [JsonPropertyName("sidebarGradientFrom")]
    public string? SidebarGradientFrom { get; set; }

    [JsonPropertyName("sidebarGradientTo")]
    public string? SidebarGradientTo { get; set; }

    /// <summary>
    /// Optional gradient start position in percent (0-100). The design boards
    /// anchor the first color stop away from 0% (History 9.39% / rail 16.89%);
    /// the shell paints one shared viewport-anchored gradient, so a single
    /// unified stop is stored here.
    /// </summary>
    [JsonPropertyName("sidebarGradientFromStop")]
    public string? SidebarGradientFromStop { get; set; }

    [JsonPropertyName("sidebarSelected")]
    public string? SidebarSelected { get; set; }

    [JsonPropertyName("sidebarInput")]
    public string? SidebarInput { get; set; }

    [JsonPropertyName("sidebarInputBorder")]
    public string? SidebarInputBorder { get; set; }

    [JsonPropertyName("chrome")]
    public string? Chrome { get; set; }

    [JsonPropertyName("workspace")]
    public string? Workspace { get; set; }

    [JsonPropertyName("tabActive")]
    public string? TabActive { get; set; }

    [JsonPropertyName("tabActiveTerminal")]
    public string? TabActiveTerminal { get; set; }

    [JsonPropertyName("composerBg")]
    public string? ComposerBg { get; set; }

    [JsonPropertyName("composerBorder")]
    public string? ComposerBorder { get; set; }

    [JsonPropertyName("composerShadow")]
    public string? ComposerShadow { get; set; }

    [JsonPropertyName("terminalBackground")]
    public string? TerminalBackground { get; set; }
}
