namespace PSX.Models;

public sealed class AppSettings
{
    public string ActiveThemeKey { get; set; } = "";
    public string ThemeFingerprint { get; set; } = "";
    public string DefaultShellProfileId { get; set; } = "powershell";
    public int FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Cascadia Code, Consolas, monospace";
    public string Theme { get; set; } = "dark";
    public int Scrollback { get; set; } = 10000;
    public int AgentFontSize { get; set; } = 15;
    public string AgentFontFamily { get; set; } = "Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Microsoft YaHei, sans-serif";
    public string AgentMonoFontFamily { get; set; } = "";
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public double WindowX { get; set; }
    public double WindowY { get; set; }

    public ThemeColors ThemeColors { get; set; } = new();
    public AgentThemeColors AgentTheme { get; set; } = new();
    public TerminalPalette TerminalColors { get; set; } = new();
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

    public static AppearanceSettings FromSettings(AppSettings settings) => new()
    {
        TerminalFontSize = settings.FontSize,
        TerminalFontFamily = settings.FontFamily,
        AgentFontSize = settings.AgentFontSize,
        AgentFontFamily = settings.AgentFontFamily,
        AgentMonoFontFamily = settings.AgentMonoFontFamily,
        ThemeColors = Clone(settings.ThemeColors),
        AgentTheme = Clone(settings.AgentTheme),
        TerminalColors = Clone(settings.TerminalColors)
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

public sealed class ThemeColors
{
    public string Background { get; set; } = "#1d1d1a";
    public string Surface { get; set; } = "#24231f";
    public string SurfaceRaised { get; set; } = "#2b2a25";
    public string SurfaceMuted { get; set; } = "#34332d";
    public string Hover { get; set; } = "#35342e";
    public string Border { get; set; } = "#3d3b34";
    public string BorderStrong { get; set; } = "#504d43";
    public string Text { get; set; } = "#e8e3d6";
    public string TextMuted { get; set; } = "#aaa396";
    public string TextDim { get; set; } = "#7f786d";
    public string Accent { get; set; } = "#5aa6a0";
    public string AccentHover { get; set; } = "#6db9b2";
    public string Error { get; set; } = "#df7c7c";
    public string ErrorBg { get; set; } = "#352220";
    public string Warning { get; set; } = "#d3ae68";
    public string WarningBg { get; set; } = "#312a1c";
    public string Scrollbar { get; set; } = "#565246";
    public string ScrollbarHover { get; set; } = "#6a6658";
}

public sealed class AgentThemeColors
{
    public string CodeBlockBg { get; set; } = "#25251f";
    public string CodeBlockText { get; set; } = "#f3ead7";
    public string CodeBlockBorder { get; set; } = "#514d3f";
    public string SendBtn { get; set; } = "#2e706c";
    public string SendBtnHover { get; set; } = "#36817c";
    public string SendBtnText { get; set; } = "#ffffff";
    public string DecisionPrimary { get; set; } = "#2e706c";
    public string DecisionPrimaryHover { get; set; } = "#36817c";
    public string DecisionPrimaryText { get; set; } = "#ffffff";
    public string StopBtn { get; set; } = "#7b3835";
    public string StopBtnHover { get; set; } = "#8d4642";
    public string AllowColor { get; set; } = "#4ade80";
    public string DenyColor { get; set; } = "#f87171";
    public string CautionColor { get; set; } = "#facc15";
    public string PermissionBg { get; set; } = "#272318";
    public string PermissionBorder { get; set; } = "#6b5b36";
    public string ElicitationBg { get; set; } = "#1d2927";
    public string ElicitationBorder { get; set; } = "#45645f";
    public string Overlay { get; set; } = "#000000";
    public string Shadow { get; set; } = "#000000";
    public string FocusRing { get; set; } = "#5aa6a0";
    // Optional workbench backdrop tint painted as a top-down gradient behind
    // the two Agent panels. RRGGBBAA; fully transparent means "no tint".
    public string WorkbenchTint { get; set; } = "#00000000";
}

public sealed class TerminalPalette
{
    public string Foreground { get; set; } = "#cdd6f4";
    public string Cursor { get; set; } = "#f5e0dc";
    public string CursorAccent { get; set; } = "#1d1d1a";
    public string SelectionBackground { get; set; } = "#585b7066";
    public string SelectionForeground { get; set; } = "#cdd6f4";
    public string Black { get; set; } = "#45475a";
    public string Red { get; set; } = "#f38ba8";
    public string Green { get; set; } = "#a6e3a1";
    public string Yellow { get; set; } = "#f9e2af";
    public string Blue { get; set; } = "#89b4fa";
    public string Magenta { get; set; } = "#f5c2e7";
    public string Cyan { get; set; } = "#94e2d5";
    public string White { get; set; } = "#bac2de";
    public string BrightBlack { get; set; } = "#585b70";
    public string BrightRed { get; set; } = "#f38ba8";
    public string BrightGreen { get; set; } = "#a6e3a1";
    public string BrightYellow { get; set; } = "#f9e2af";
    public string BrightBlue { get; set; } = "#89b4fa";
    public string BrightMagenta { get; set; } = "#f5c2e7";
    public string BrightCyan { get; set; } = "#94e2d5";
    public string BrightWhite { get; set; } = "#a6adc8";
}
