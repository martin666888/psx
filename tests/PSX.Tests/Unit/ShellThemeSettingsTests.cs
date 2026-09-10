using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ShellThemePresetTests
{
    [TestMethod]
    public void LoadTheme_AllBundledPresets_AreValid()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadTheme_AllBundledPresets_AreValid));
        var presetsDirectory = Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets");
        var service = new ThemeService(presetsDirectory, Path.Combine(workspace.Path, "user"));

        var themes = service.ScanThemes().Where(t => t.Source == ThemeSource.BuiltIn).ToArray();

        Assert.HasCount(5, themes);
        Assert.IsTrue(
            themes.All(t => t.Availability == ThemeAvailability.Available),
            string.Join(Environment.NewLine, themes.Select(t => $"{t.Id}: {t.DiagnosticSummary}")));
        Assert.IsTrue(themes.Any(t => t.Id == "base-light"));
        Assert.IsTrue(themes.Any(t => t.Id == "meadow-green"));
        Assert.IsTrue(themes.Any(t => t.Id == "parchment"));
        Assert.IsTrue(themes.Any(t => t.Id == "vercel-black"));
        Assert.IsTrue(themes.Any(t => t.Id == "charcoal"));
    }

    [TestMethod]
    public void LoadTheme_MissingShellThemeSection_ShellThemeFieldsStayNull()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadTheme_MissingShellThemeSection_ShellThemeFieldsStayNull));
        var themePath = Path.Combine(workspace.Path, "noshell.ini");
        var source = File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets", "base-light.ini"));
        File.WriteAllText(themePath, RemoveSection(source, "shellTheme"));
        var service = new ThemeService(workspace.Path, Path.Combine(workspace.Path, "user"));

        var result = service.LoadTheme(themePath, ThemeSource.User);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.Descriptor.Appearance);
        AssertShellThemeAllNull(result.Descriptor.Appearance.ShellTheme);
        Assert.IsFalse(result.Descriptor.Diagnostics.Any(d => d.Message.Contains("shellTheme", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LoadTheme_InvalidShellThemeColor_WarnsAndIgnoresOnlyThatKey()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadTheme_InvalidShellThemeColor_WarnsAndIgnoresOnlyThatKey));
        var themePath = Path.Combine(workspace.Path, "badshell.ini");
        var source = File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets", "base-light.ini"));
        File.WriteAllText(themePath, source.Replace("sidebar=#f7f7fa", "sidebar=not-a-color", StringComparison.Ordinal));
        var service = new ThemeService(workspace.Path, Path.Combine(workspace.Path, "user"));

        var result = service.LoadTheme(themePath, ThemeSource.User);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.Descriptor.Appearance);
        Assert.IsNull(result.Descriptor.Appearance.ShellTheme.Sidebar);
        Assert.AreEqual("#ffffff", result.Descriptor.Appearance.ShellTheme.SidebarInput);
        StringAssert.Contains(result.Descriptor.DiagnosticSummary, "[shellTheme].sidebar must be #RRGGBB");
    }

    [TestMethod]
    public void LoadTheme_ValidShellThemeSection_FieldsAreParsed()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadTheme_ValidShellThemeSection_FieldsAreParsed));
        var themePath = Path.Combine(workspace.Path, "vercel-black.ini");
        File.Copy(
            Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets", "vercel-black.ini"),
            themePath);
        var service = new ThemeService(workspace.Path, Path.Combine(workspace.Path, "user"));

        var result = service.LoadTheme(themePath, ThemeSource.User);

        Assert.IsTrue(result.IsValid);
        var shell = result.Descriptor.Appearance!.ShellTheme;
        Assert.AreEqual("#141414", shell.Sidebar);
        Assert.AreEqual("#e8e8e8", shell.TabActiveTerminal);
        Assert.AreEqual("#0000000f", shell.ComposerShadow);
        Assert.IsNull(shell.SidebarGradientFrom);
    }

    [TestMethod]
    public void LoadTheme_GradientStopPercent_ParsedAndValidated()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadTheme_GradientStopPercent_ParsedAndValidated));
        var themePath = Path.Combine(workspace.Path, "meadow-green.ini");
        File.Copy(
            Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets", "meadow-green.ini"),
            themePath);
        var service = new ThemeService(workspace.Path, Path.Combine(workspace.Path, "user"));

        var result = service.LoadTheme(themePath, ThemeSource.User);

        Assert.IsTrue(result.IsValid);
        var shell = result.Descriptor.Appearance!.ShellTheme;
        Assert.AreEqual("#f3f8ec", shell.SidebarGradientFrom);
        Assert.AreEqual("#e5f8d1", shell.SidebarGradientTo);
        Assert.AreEqual("9.39", shell.SidebarGradientFromStop);

        // Out-of-range stops are ignored with a warning, never a load failure.
        File.WriteAllText(
            themePath,
            File.ReadAllText(themePath).Replace(
                "sidebarGradientFromStop=9.39", "sidebarGradientFromStop=250", StringComparison.Ordinal));
        var invalid = service.LoadTheme(themePath, ThemeSource.User);
        Assert.IsTrue(invalid.IsValid);
        Assert.IsNull(invalid.Descriptor.Appearance!.ShellTheme.SidebarGradientFromStop);
        StringAssert.Contains(
            invalid.Descriptor.DiagnosticSummary,
            "[shellTheme].sidebarGradientFromStop must be a percentage");
    }

    [TestMethod]
    public void ComputeFingerprint_ShellThemeChangesProduceDifferentFingerprints()
    {
        using var workspace = TestWorkspace.Create(nameof(ComputeFingerprint_ShellThemeChangesProduceDifferentFingerprints));
        var service = new ThemeService(workspace.Path, Path.Combine(workspace.Path, "user"));
        var withoutShell = AppearanceSettings.FromSettings(new AppSettings());
        var withShell = withoutShell.Clone();
        withShell.ShellTheme.Sidebar = "#f7f7fa";

        var noShellFingerprint = service.ComputeFingerprint(withoutShell);

        Assert.AreNotEqual(noShellFingerprint, service.ComputeFingerprint(withShell));
        withShell.ShellTheme.Sidebar = "#141414";
        Assert.AreNotEqual(service.ComputeFingerprint(withShell), service.ComputeFingerprint(withoutShell.Clone()));
        var changed = withShell.Clone();
        changed.ShellTheme.Sidebar = "#222529";
        Assert.AreNotEqual(service.ComputeFingerprint(withShell), service.ComputeFingerprint(changed));
        Assert.AreEqual(service.ComputeFingerprint(withShell), service.ComputeFingerprint(withShell.Clone()));
    }

    private static string RemoveSection(string source, string section)
    {
        var lines = source.Split('\n');
        var kept = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                inside = string.Equals(trimmed[1..^1], section, StringComparison.OrdinalIgnoreCase);
            if (!inside)
                kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    private static void AssertShellThemeAllNull(ShellThemeColors shell)
    {
        Assert.IsNull(shell.Sidebar);
        Assert.IsNull(shell.SidebarGradientFrom);
        Assert.IsNull(shell.SidebarGradientTo);
        Assert.IsNull(shell.SidebarGradientFromStop);
        Assert.IsNull(shell.SidebarSelected);
        Assert.IsNull(shell.SidebarInput);
        Assert.IsNull(shell.SidebarInputBorder);
        Assert.IsNull(shell.Chrome);
        Assert.IsNull(shell.Workspace);
        Assert.IsNull(shell.TabActive);
        Assert.IsNull(shell.TabActiveTerminal);
        Assert.IsNull(shell.ComposerBg);
        Assert.IsNull(shell.ComposerBorder);
        Assert.IsNull(shell.ComposerShadow);
        Assert.IsNull(shell.TerminalBackground);
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class ShellThemeSettingsTests
{
    [TestMethod]
    [DataRow("builtin:dark", "builtin:vercel-black")]
    [DataRow("builtin:vercel-neutral-dark", "builtin:vercel-black")]
    [DataRow("builtin:terminal-green", "builtin:meadow-green")]
    [DataRow("builtin:warm-light", "builtin:base-light")]
    [DataRow("builtin:vercel-neutral-light", "builtin:base-light")]
    public void GetSettings_LegacyActiveThemeKey_MigratesAndPersistsOnce(string legacyKey, string expectedKey)
    {
        using var workspace = TestWorkspace.Create($"migration-{legacyKey}-{expectedKey}");
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        WriteConfigWithThemeKey(configPath, legacyKey, fontFamily: "Custom Mono, monospace");

        var loaded = new SettingsService(configPath).GetSettings();

        var presetPath = Path.Combine(
            TestWorkspace.RepositoryRoot, "theme-presets", expectedKey["builtin:".Length..] + ".ini");
        var preset = new ThemeService().LoadTheme(presetPath, ThemeSource.BuiltIn);
        Assert.IsTrue(preset.IsValid, preset.Descriptor.DiagnosticSummary);
        Assert.IsNotNull(preset.Descriptor.Appearance);
        Assert.AreEqual(expectedKey, loaded.ActiveThemeKey);
        Assert.AreEqual(preset.Descriptor.Fingerprint, loaded.ThemeFingerprint);
        // The successor preset's colors are applied immediately…
        Assert.AreEqual(preset.Descriptor.Appearance.ThemeColors.Background, loaded.ThemeColors.Background);
        Assert.AreEqual(preset.Descriptor.Appearance.AgentTheme.CodeBlockBg, loaded.AgentTheme.CodeBlockBg);
        Assert.AreEqual(preset.Descriptor.Appearance.TerminalColors.Foreground, loaded.TerminalColors.Foreground);
        Assert.AreEqual(preset.Descriptor.Appearance.ShellTheme.Sidebar, loaded.ShellTheme.Sidebar);
        // …while user font choices survive the migration.
        Assert.AreEqual("Custom Mono, monospace", loaded.FontFamily);
        var persisted = File.ReadAllText(configPath);
        StringAssert.Contains(persisted, $"activeThemeKey={expectedKey}");
        Assert.IsFalse(persisted.Contains(legacyKey, StringComparison.OrdinalIgnoreCase));

        // Idempotent: a second load keeps the migrated key and the file bytes.
        var second = new SettingsService(configPath).GetSettings();
        Assert.AreEqual(expectedKey, second.ActiveThemeKey);
        Assert.AreEqual(persisted, File.ReadAllText(configPath));
    }

    [TestMethod]
    public void GetSettings_CustomActiveThemeKey_IsNeverMigrated()
    {
        using var workspace = TestWorkspace.Create(nameof(GetSettings_CustomActiveThemeKey_IsNeverMigrated));
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        WriteConfigWithThemeKey(configPath, "user:my-own-theme");

        var loaded = new SettingsService(configPath).GetSettings();

        Assert.AreEqual("user:my-own-theme", loaded.ActiveThemeKey);
    }

    [TestMethod]
    public void SaveAndLoadSettings_ShellThemeRoundTrips()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveAndLoadSettings_ShellThemeRoundTrips));
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        var service = new SettingsService(configPath);
        var settings = new AppSettings
        {
            ActiveThemeKey = "builtin:base-light",
            ThemeFingerprint = "abc123"
        };
        settings.ShellTheme.Sidebar = "#f7f7fa";
        settings.ShellTheme.SidebarGradientFrom = "#f3f8ec";
        settings.ShellTheme.SidebarGradientTo = "#e5f8d1";
        settings.ShellTheme.SidebarGradientFromStop = "9.39";
        settings.ShellTheme.SidebarSelected = "#e8ebf0";
        settings.ShellTheme.SidebarInput = "#ffffff";
        settings.ShellTheme.SidebarInputBorder = "#dee0e5";
        settings.ShellTheme.Chrome = "#f0f0f2";
        settings.ShellTheme.Workspace = "#ffffff";
        settings.ShellTheme.TabActive = "#ffffff";
        settings.ShellTheme.TabActiveTerminal = "#e8e8e8";
        settings.ShellTheme.ComposerBg = "#171717";
        settings.ShellTheme.ComposerBorder = "#c6c8d0b3";
        settings.ShellTheme.ComposerShadow = "#0000000f";
        settings.ShellTheme.TerminalBackground = "#141414";

        service.SaveSettings(settings);

        var loaded = new SettingsService(configPath).ReloadSettings();
        Assert.AreEqual("#f7f7fa", loaded.ShellTheme.Sidebar);
        Assert.AreEqual("#f3f8ec", loaded.ShellTheme.SidebarGradientFrom);
        Assert.AreEqual("#e5f8d1", loaded.ShellTheme.SidebarGradientTo);
        Assert.AreEqual("9.39", loaded.ShellTheme.SidebarGradientFromStop);
        Assert.AreEqual("#e8ebf0", loaded.ShellTheme.SidebarSelected);
        Assert.AreEqual("#ffffff", loaded.ShellTheme.SidebarInput);
        Assert.AreEqual("#dee0e5", loaded.ShellTheme.SidebarInputBorder);
        Assert.AreEqual("#f0f0f2", loaded.ShellTheme.Chrome);
        Assert.AreEqual("#ffffff", loaded.ShellTheme.Workspace);
        Assert.AreEqual("#ffffff", loaded.ShellTheme.TabActive);
        Assert.AreEqual("#e8e8e8", loaded.ShellTheme.TabActiveTerminal);
        Assert.AreEqual("#171717", loaded.ShellTheme.ComposerBg);
        Assert.AreEqual("#c6c8d0b3", loaded.ShellTheme.ComposerBorder);
        Assert.AreEqual("#0000000f", loaded.ShellTheme.ComposerShadow);
        Assert.AreEqual("#141414", loaded.ShellTheme.TerminalBackground);
    }

    [TestMethod]
    public void LoadSettings_IniWithoutShellThemeSection_ShellThemeStaysEmpty()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadSettings_IniWithoutShellThemeSection_ShellThemeStaysEmpty));
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        WriteConfigWithThemeKey(configPath, "builtin:base-light");

        var loaded = new SettingsService(configPath).GetSettings();

        Assert.IsFalse(File.ReadAllText(configPath).Contains("[shellTheme]", StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(loaded.ShellTheme.Sidebar);
        Assert.IsNull(loaded.ShellTheme.ComposerBorder);
    }

    [TestMethod]
    public void SaveSettings_AllShellThemeFieldsEmpty_WritesNoShellThemeSection()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveSettings_AllShellThemeFieldsEmpty_WritesNoShellThemeSection));
        var configPath = Path.Combine(workspace.Path, "psx.ini");

        new SettingsService(configPath).SaveSettings(new AppSettings());

        Assert.IsFalse(File.ReadAllText(configPath).Contains("[shellTheme]", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteConfigWithThemeKey(string configPath, string activeThemeKey, string? fontFamily = null)
    {
        var service = new SettingsService(configPath);
        var settings = new AppSettings();
        if (fontFamily != null)
            settings.FontFamily = fontFamily;
        service.SaveSettings(settings);
        File.WriteAllText(configPath, ReplaceActiveThemeKey(File.ReadAllText(configPath), activeThemeKey));
    }

    private static string ReplaceActiveThemeKey(string ini, string activeThemeKey)
    {
        var lines = ini.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("activeThemeKey=", StringComparison.Ordinal))
                lines[i] = $"activeThemeKey={activeThemeKey}";
        }
        return string.Join('\n', lines);
    }
}
