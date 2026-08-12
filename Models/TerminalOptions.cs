using System.Text.Json.Serialization;

namespace PSX.Models;

public sealed class TerminalOptions
{
    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; }

    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "";

    [JsonPropertyName("scrollback")]
    public int Scrollback { get; set; }

    [JsonPropertyName("windowsBuildNumber")]
    public int WindowsBuildNumber { get; set; }

    [JsonPropertyName("agentFontSize")]
    public int AgentFontSize { get; set; }

    [JsonPropertyName("agentFontFamily")]
    public string AgentFontFamily { get; set; } = "";

    [JsonPropertyName("agentMonoFontFamily")]
    public string AgentMonoFontFamily { get; set; } = "";

    [JsonPropertyName("themeColors")]
    public ThemeColors? ThemeColors { get; set; }

    [JsonPropertyName("agentThemeColors")]
    public AgentThemeColors? AgentThemeColors { get; set; }

    [JsonPropertyName("terminalColors")]
    public TerminalPalette? TerminalColors { get; set; }

    public static TerminalOptions FromSettings(AppSettings settings)
    {
        return new TerminalOptions
        {
            FontSize = settings.FontSize,
            FontFamily = settings.FontFamily,
            Theme = settings.Theme,
            Scrollback = settings.Scrollback,
            WindowsBuildNumber = Environment.OSVersion.Version.Build,
            AgentFontSize = settings.AgentFontSize,
            AgentFontFamily = settings.AgentFontFamily,
            AgentMonoFontFamily = string.IsNullOrWhiteSpace(settings.AgentMonoFontFamily)
                ? AppSettings.BundledAgentMonoFontFamily
                : settings.AgentMonoFontFamily,
            ThemeColors = settings.ThemeColors,
            AgentThemeColors = settings.AgentTheme,
            TerminalColors = settings.TerminalColors
        };
    }
}
