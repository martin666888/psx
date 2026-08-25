using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

public interface ISettingsService
{
    AppSettings GetSettings();
    AppSettings ReloadSettings();
    void SaveSettings(AppSettings settings);
    void SaveThemeSettings(AppSettings settings, string activeThemeKey, string themeFingerprint);
    string ConfigPath { get; }
    /// <summary>Fixed startup-warning code ('with_backup' | 'safe_defaults' |
    /// 'backup_also_invalid'); the sentence is formatted from .resx at render
    /// time so a locale switch re-localizes it.</summary>
    string? StartupWarningCode { get; }
    /// <summary>Raw technical detail (exception message) interpolated as {0}.</summary>
    string? StartupWarningDetail { get; }
    List<ShellProfile> GetProfiles();
    ShellProfile GetDefaultProfile();
}

public sealed class SettingsService : ISettingsService
{
    private readonly string _configPath;
    private AppSettings? _settings;
    private List<ShellProfile>? _profiles;
    public string ConfigPath => _configPath;
    public string? StartupWarningCode { get; private set; }
    public string? StartupWarningDetail { get; private set; }

    public SettingsService()
        : this(Path.Combine(AppContext.BaseDirectory, "psx.ini"))
    {
    }

    internal SettingsService(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        _configPath = Path.GetFullPath(configPath);
    }

    public AppSettings GetSettings()
    {
        if (_settings != null) return _settings;

        if (!File.Exists(_configPath))
        {
            _settings = new AppSettings();
            NormalizeAgentSettings(_settings);
            return _settings;
        }

        try
        {
            _settings = LoadSettingsFile(_configPath);
        }
        catch (Exception activeError)
        {
            var backupPath = _configPath + ".bak";
            try
            {
                _settings = File.Exists(backupPath) ? LoadSettingsFile(backupPath) : new AppSettings();
                StartupWarningCode = File.Exists(backupPath) ? "with_backup" : "safe_defaults";
                StartupWarningDetail = activeError.Message;
            }
            catch (Exception backupError)
            {
                _settings = new AppSettings();
                StartupWarningCode = "backup_also_invalid";
                StartupWarningDetail = backupError.Message;
            }
        }

        NormalizeAgentSettings(_settings);
        return _settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        NormalizeAgentSettings(settings);
        var ini = BuildIni(settings);
        var tmpPath = _configPath + ".tmp";
        try
        {
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(ini);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            ValidateManagedIni(File.ReadAllLines(tmpPath, Encoding.UTF8), Path.GetFileName(tmpPath));
            if (File.Exists(_configPath))
                File.Replace(tmpPath, _configPath, _configPath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmpPath, _configPath);
            _settings = settings;
        }
        finally
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
    }

    public void SaveThemeSettings(AppSettings settings, string activeThemeKey, string themeFingerprint)
    {
        settings.ActiveThemeKey = activeThemeKey;
        settings.ThemeFingerprint = themeFingerprint;
        SaveSettings(settings);
    }

    public AppSettings ReloadSettings()
    {
        var settings = LoadSettingsFile(_configPath);
        NormalizeAgentSettings(settings);
        return settings;
    }

    public List<ShellProfile> GetProfiles()
    {
        if (_profiles != null) return _profiles;

        _profiles = new List<ShellProfile>
        {
            ShellProfile.Cmd,
            ShellProfile.PowerShell
        };

        return _profiles;
    }

    public ShellProfile GetDefaultProfile()
    {
        var settings = GetSettings();
        var profiles = GetProfiles();
        return profiles.FirstOrDefault(p => p.Id == settings.DefaultShellProfileId) ?? ShellProfile.PowerShell;
    }

    private static void LoadIni(AppSettings settings, IEnumerable<string> lines)
    {
        var section = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim().ToLowerInvariant();
            var value = line[(separatorIndex + 1)..].Trim();

            switch (section)
            {
                case "meta":
                    ApplyMetaSetting(settings, key, value);
                    break;
                case "terminal":
                    ApplyTerminalSetting(settings, key, value);
                    break;
                case "agent":
                    ApplyAgentSetting(settings, key, value);
                    break;
                case "shell":
                    ApplyShellSetting(settings, key, value);
                    break;
                case "theme":
                    ApplyThemeSetting(settings, key, value);
                    break;
                case "agenttheme":
                    ApplyAgentThemeSetting(settings, key, value);
                    break;
                case "terminalcolors":
                    ApplyTerminalColorSetting(settings, key, value);
                    break;
            }
        }
    }

    private static void ApplyMetaSetting(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "activethemekey": settings.ActiveThemeKey = value; break;
            case "themefingerprint": settings.ThemeFingerprint = value; break;
        }
    }

    private static void ApplyTerminalSetting(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "fontsize":
                if (int.TryParse(value, out var fontSize) && fontSize is >= 6 and <= 72)
                    settings.FontSize = fontSize;
                break;
            case "fontfamily":
                if (!string.IsNullOrWhiteSpace(value))
                    settings.FontFamily = value;
                break;
            case "scrollback":
                if (int.TryParse(value, out var scrollback) && scrollback is >= 0 and <= 1_000_000)
                    settings.Scrollback = scrollback;
                break;
            case "theme":
                if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase))
                    settings.Theme = "dark";
                break;
        }
    }

    private static void ApplyAgentSetting(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "fontsize":
                if (int.TryParse(value, out var fontSize) && fontSize is >= 6 and <= 72)
                    settings.AgentFontSize = fontSize;
                break;
            case "fontfamily":
                if (!string.IsNullOrWhiteSpace(value))
                    settings.AgentFontFamily = value;
                break;
            case "monofontfamily":
                if (!string.IsNullOrWhiteSpace(value))
                    settings.AgentMonoFontFamily = value;
                break;
        }
    }

    private static void ApplyShellSetting(AppSettings settings, string key, string value)
    {
        if (key != "defaultprofile" || string.IsNullOrWhiteSpace(value))
            return;

        settings.DefaultShellProfileId = value.Trim().ToLowerInvariant();
    }

    private static void ApplyThemeSetting(AppSettings settings, string key, string value)
    {
        var t = settings.ThemeColors;
        switch (key)
        {
            case "background": t.Background = ValidateColor(value, t.Background); break;
            case "surface": t.Surface = ValidateColor(value, t.Surface); break;
            case "surfaceraised": t.SurfaceRaised = ValidateColor(value, t.SurfaceRaised); break;
            case "surfacemuted": t.SurfaceMuted = ValidateColor(value, t.SurfaceMuted); break;
            case "hover": t.Hover = ValidateColor(value, t.Hover); break;
            case "border": t.Border = ValidateColor(value, t.Border); break;
            case "borderstrong": t.BorderStrong = ValidateColor(value, t.BorderStrong); break;
            case "text": t.Text = ValidateColor(value, t.Text); break;
            case "textmuted": t.TextMuted = ValidateColor(value, t.TextMuted); break;
            case "textdim": t.TextDim = ValidateColor(value, t.TextDim); break;
            case "accent": t.Accent = ValidateColor(value, t.Accent); break;
            case "accenthover": t.AccentHover = ValidateColor(value, t.AccentHover); break;
            case "error": t.Error = ValidateColor(value, t.Error); break;
            case "errorbg": t.ErrorBg = ValidateColor(value, t.ErrorBg); break;
            case "warning": t.Warning = ValidateColor(value, t.Warning); break;
            case "warningbg": t.WarningBg = ValidateColor(value, t.WarningBg); break;
            case "scrollbar": t.Scrollbar = ValidateColor(value, t.Scrollbar); break;
            case "scrollbarhover": t.ScrollbarHover = ValidateColor(value, t.ScrollbarHover); break;
        }
    }

    private static void ApplyAgentThemeSetting(AppSettings settings, string key, string value)
    {
        var a = settings.AgentTheme;
        switch (key)
        {
            case "codeblockbg": a.CodeBlockBg = ValidateColor(value, a.CodeBlockBg); break;
            case "codeblocktext": a.CodeBlockText = ValidateColor(value, a.CodeBlockText); break;
            case "codeblockborder": a.CodeBlockBorder = ValidateColor(value, a.CodeBlockBorder); break;
            case "sendbtn": a.SendBtn = ValidateColor(value, a.SendBtn); break;
            case "sendbtnhover": a.SendBtnHover = ValidateColor(value, a.SendBtnHover); break;
            case "sendbtntext": a.SendBtnText = ValidateColor(value, a.SendBtnText); break;
            case "decisionprimary": a.DecisionPrimary = ValidateColor(value, a.DecisionPrimary); break;
            case "decisionprimaryhover": a.DecisionPrimaryHover = ValidateColor(value, a.DecisionPrimaryHover); break;
            case "decisionprimarytext": a.DecisionPrimaryText = ValidateColor(value, a.DecisionPrimaryText); break;
            case "stopbtn": a.StopBtn = ValidateColor(value, a.StopBtn); break;
            case "stopbtnhover": a.StopBtnHover = ValidateColor(value, a.StopBtnHover); break;
            case "allowcolor": a.AllowColor = ValidateColor(value, a.AllowColor); break;
            case "denycolor": a.DenyColor = ValidateColor(value, a.DenyColor); break;
            case "cautioncolor": a.CautionColor = ValidateColor(value, a.CautionColor); break;
            case "permissionbg": a.PermissionBg = ValidateColor(value, a.PermissionBg); break;
            case "permissionborder": a.PermissionBorder = ValidateColor(value, a.PermissionBorder); break;
            case "elicitationbg": a.ElicitationBg = ValidateColor(value, a.ElicitationBg); break;
            case "elicitationborder": a.ElicitationBorder = ValidateColor(value, a.ElicitationBorder); break;
            case "overlay": a.Overlay = ValidateColor(value, a.Overlay); break;
            case "shadow": a.Shadow = ValidateColor(value, a.Shadow); break;
            case "focusring": a.FocusRing = ValidateColor(value, a.FocusRing); break;
            case "workbenchtint": a.WorkbenchTint = ValidateColor(value, a.WorkbenchTint); break;
        }
    }

    private static void ApplyTerminalColorSetting(AppSettings settings, string key, string value)
    {
        var p = settings.TerminalColors;
        switch (key)
        {
            case "foreground": p.Foreground = ValidateColor(value, p.Foreground); break;
            case "cursor": p.Cursor = ValidateColor(value, p.Cursor); break;
            case "cursoraccent": p.CursorAccent = ValidateColor(value, p.CursorAccent); break;
            case "selectionbackground": p.SelectionBackground = ValidateColor(value, p.SelectionBackground); break;
            case "selectionforeground": p.SelectionForeground = ValidateColor(value, p.SelectionForeground); break;
            case "black": p.Black = ValidateColor(value, p.Black); break;
            case "red": p.Red = ValidateColor(value, p.Red); break;
            case "green": p.Green = ValidateColor(value, p.Green); break;
            case "yellow": p.Yellow = ValidateColor(value, p.Yellow); break;
            case "blue": p.Blue = ValidateColor(value, p.Blue); break;
            case "magenta": p.Magenta = ValidateColor(value, p.Magenta); break;
            case "cyan": p.Cyan = ValidateColor(value, p.Cyan); break;
            case "white": p.White = ValidateColor(value, p.White); break;
            case "brightblack": p.BrightBlack = ValidateColor(value, p.BrightBlack); break;
            case "brightred": p.BrightRed = ValidateColor(value, p.BrightRed); break;
            case "brightgreen": p.BrightGreen = ValidateColor(value, p.BrightGreen); break;
            case "brightyellow": p.BrightYellow = ValidateColor(value, p.BrightYellow); break;
            case "brightblue": p.BrightBlue = ValidateColor(value, p.BrightBlue); break;
            case "brightmagenta": p.BrightMagenta = ValidateColor(value, p.BrightMagenta); break;
            case "brightcyan": p.BrightCyan = ValidateColor(value, p.BrightCyan); break;
            case "brightwhite": p.BrightWhite = ValidateColor(value, p.BrightWhite); break;
        }
    }

    private static string ValidateColor(string value, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var trimmed = value.Trim();
        return Regex.IsMatch(trimmed, @"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")
            ? trimmed.ToLowerInvariant()
            : defaultValue;
    }

    private static void NormalizeAgentSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentMonoFontFamily))
        {
            settings.AgentMonoFontFamily = AppSettings.BundledAgentMonoFontFamily;
        }
    }

    private static string BuildIni(AppSettings settings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("; PSX 核心活动配置文件，由程序维护，请勿直接修改主题字段。");
        builder.AppendLine("; 自定义颜色和字体请编辑用户主题文件；高级非主题设置可谨慎修改。");
        builder.AppendLine();
        builder.AppendLine("[meta]");
        builder.AppendLine("kind=active-config");
        builder.AppendLine("schemaVersion=1");
        builder.AppendLine($"activeThemeKey={settings.ActiveThemeKey}");
        builder.AppendLine($"themeFingerprint={settings.ThemeFingerprint}");
        builder.AppendLine();
        builder.AppendLine("[terminal]");
        builder.AppendLine($"fontSize={settings.FontSize}");
        builder.AppendLine($"fontFamily={settings.FontFamily}");
        builder.AppendLine($"scrollback={settings.Scrollback}");
        builder.AppendLine($"theme={settings.Theme}");
        builder.AppendLine();
        builder.AppendLine("[agent]");
        builder.AppendLine($"fontSize={settings.AgentFontSize}");
        builder.AppendLine($"fontFamily={settings.AgentFontFamily}");
        builder.AppendLine($"monoFontFamily={settings.AgentMonoFontFamily}");
        builder.AppendLine();
        builder.AppendLine("[shell]");
        builder.AppendLine($"defaultProfile={settings.DefaultShellProfileId}");
        builder.AppendLine();

        // Theme
        var t = settings.ThemeColors;
        builder.AppendLine("[theme]");
        builder.AppendLine($"background={t.Background}");
        builder.AppendLine($"surface={t.Surface}");
        builder.AppendLine($"surfaceRaised={t.SurfaceRaised}");
        builder.AppendLine($"surfaceMuted={t.SurfaceMuted}");
        builder.AppendLine($"hover={t.Hover}");
        builder.AppendLine($"border={t.Border}");
        builder.AppendLine($"borderStrong={t.BorderStrong}");
        builder.AppendLine($"text={t.Text}");
        builder.AppendLine($"textMuted={t.TextMuted}");
        builder.AppendLine($"textDim={t.TextDim}");
        builder.AppendLine($"accent={t.Accent}");
        builder.AppendLine($"accentHover={t.AccentHover}");
        builder.AppendLine($"error={t.Error}");
        builder.AppendLine($"errorBg={t.ErrorBg}");
        builder.AppendLine($"warning={t.Warning}");
        builder.AppendLine($"warningBg={t.WarningBg}");
        builder.AppendLine($"scrollbar={t.Scrollbar}");
        builder.AppendLine($"scrollbarHover={t.ScrollbarHover}");
        builder.AppendLine();

        // Agent Theme
        var a = settings.AgentTheme;
        builder.AppendLine("[agentTheme]");
        builder.AppendLine($"codeBlockBg={a.CodeBlockBg}");
        builder.AppendLine($"codeBlockText={a.CodeBlockText}");
        builder.AppendLine($"codeBlockBorder={a.CodeBlockBorder}");
        builder.AppendLine($"sendBtn={a.SendBtn}");
        builder.AppendLine($"sendBtnHover={a.SendBtnHover}");
        builder.AppendLine($"sendBtnText={a.SendBtnText}");
        builder.AppendLine($"decisionPrimary={a.DecisionPrimary}");
        builder.AppendLine($"decisionPrimaryHover={a.DecisionPrimaryHover}");
        builder.AppendLine($"decisionPrimaryText={a.DecisionPrimaryText}");
        builder.AppendLine($"stopBtn={a.StopBtn}");
        builder.AppendLine($"stopBtnHover={a.StopBtnHover}");
        builder.AppendLine($"allowColor={a.AllowColor}");
        builder.AppendLine($"denyColor={a.DenyColor}");
        builder.AppendLine($"cautionColor={a.CautionColor}");
        builder.AppendLine($"permissionBg={a.PermissionBg}");
        builder.AppendLine($"permissionBorder={a.PermissionBorder}");
        builder.AppendLine($"elicitationBg={a.ElicitationBg}");
        builder.AppendLine($"elicitationBorder={a.ElicitationBorder}");
        builder.AppendLine($"overlay={a.Overlay}");
        builder.AppendLine($"shadow={a.Shadow}");
        builder.AppendLine($"focusRing={a.FocusRing}");
        builder.AppendLine($"workbenchTint={a.WorkbenchTint}");
        builder.AppendLine();

        // Terminal Colors
        var p = settings.TerminalColors;
        builder.AppendLine("[terminalColors]");
        builder.AppendLine($"foreground={p.Foreground}");
        builder.AppendLine($"cursor={p.Cursor}");
        builder.AppendLine($"cursorAccent={p.CursorAccent}");
        builder.AppendLine($"selectionBackground={p.SelectionBackground}");
        builder.AppendLine($"selectionForeground={p.SelectionForeground}");
        builder.AppendLine($"black={p.Black}");
        builder.AppendLine($"red={p.Red}");
        builder.AppendLine($"green={p.Green}");
        builder.AppendLine($"yellow={p.Yellow}");
        builder.AppendLine($"blue={p.Blue}");
        builder.AppendLine($"magenta={p.Magenta}");
        builder.AppendLine($"cyan={p.Cyan}");
        builder.AppendLine($"white={p.White}");
        builder.AppendLine($"brightBlack={p.BrightBlack}");
        builder.AppendLine($"brightRed={p.BrightRed}");
        builder.AppendLine($"brightGreen={p.BrightGreen}");
        builder.AppendLine($"brightYellow={p.BrightYellow}");
        builder.AppendLine($"brightBlue={p.BrightBlue}");
        builder.AppendLine($"brightMagenta={p.BrightMagenta}");
        builder.AppendLine($"brightCyan={p.BrightCyan}");
        builder.AppendLine($"brightWhite={p.BrightWhite}");
        return builder.ToString();
    }

    private static AppSettings LoadSettingsFile(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Any(line => string.Equals(line.Trim(), "[meta]", StringComparison.OrdinalIgnoreCase)))
            ValidateManagedIni(lines, Path.GetFileName(path));

        var settings = new AppSettings();
        LoadIni(settings, lines);
        return settings;
    }

    private static void ValidateManagedIni(string[] lines, string sourceName)
    {
        var document = IniDocument.Parse(lines, sourceName);
        if (!string.Equals(document.Get("meta", "kind"), "active-config", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("[meta].kind must be 'active-config'.");
        if (document.Get("meta", "schemaVersion") != "1")
            throw new InvalidDataException("[meta].schemaVersion must be 1.");

        RequireKeys(document, "terminal", ["fontSize", "fontFamily", "scrollback", "theme"]);
        RequireKeys(document, "agent", ["fontSize", "fontFamily", "monoFontFamily"]);
        RequireKeys(document, "shell", ["defaultProfile"]);
        RequireColorProperties<ThemeColors>(document, "theme");
        RequireColorProperties<AgentThemeColors>(document, "agentTheme");
        RequireColorProperties<TerminalPalette>(document, "terminalColors");
        ValidateInteger(document, "terminal", "fontSize", 6, 72);
        ValidateInteger(document, "agent", "fontSize", 6, 72);
        ValidateInteger(document, "terminal", "scrollback", 0, 1_000_000);
    }

    private static void RequireKeys(IniDocument document, string section, IEnumerable<string> keys)
    {
        if (!document.TryGetSection(section, out var values))
            throw new InvalidDataException($"Missing required section [{section}].");
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Missing required field [{section}].{key}.");
        }
    }

    private static void RequireColorProperties<T>(IniDocument document, string section)
    {
        var keys = typeof(T).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToArray();
        RequireKeys(document, section, keys);
        foreach (var key in keys)
        {
            var value = document.Get(section, key)!;
            if (!Regex.IsMatch(value, @"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$"))
                throw new InvalidDataException($"[{section}].{key} must be #RRGGBB or #RRGGBBAA.");
        }
    }

    private static void ValidateInteger(IniDocument document, string section, string key, int minimum, int maximum)
    {
        var raw = document.Get(section, key);
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidDataException($"[{section}].{key} must be between {minimum} and {maximum}.");
    }
}
