using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class SettingsServiceTests
{
    [TestMethod]
    public void GetSettings_MissingInjectedConfig_UsesDefaultsWithoutWritingOutsideWorkspace()
    {
        using var workspace = TestWorkspace.Create(nameof(GetSettings_MissingInjectedConfig_UsesDefaultsWithoutWritingOutsideWorkspace));
        var configPath = Path.Combine(workspace.Path, "config", "psx.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var service = new SettingsService(configPath);

        var settings = service.GetSettings();

        Assert.AreEqual(Path.GetFullPath(configPath), service.ConfigPath);
        Assert.AreEqual("powershell", settings.DefaultShellProfileId);
        Assert.AreEqual(AppSettings.AgentUiFontFamily, settings.AgentFontFamily);
        Assert.AreEqual(AppSettings.BundledAgentMonoFontFamily, settings.AgentMonoFontFamily);
        Assert.IsFalse(File.Exists(configPath));
    }

    [TestMethod]
    public void TerminalOptions_MissingAgentMono_UsesBundledAgentMonoDefault()
    {
        var settings = new AppSettings
        {
            FontFamily = "Terminal Only Mono",
            AgentMonoFontFamily = "   "
        };

        var options = TerminalOptions.FromSettings(settings);

        Assert.AreEqual(AppSettings.BundledAgentMonoFontFamily, options.AgentMonoFontFamily);
    }

    [TestMethod]
    public void SaveSettings_InjectedConfig_RoundTripsAndCreatesBackup()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveSettings_InjectedConfig_RoundTripsAndCreatesBackup));
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        var service = new SettingsService(configPath);
        var first = new AppSettings { FontSize = 17, AgentMonoFontFamily = "Test Mono" };
        service.SaveSettings(first);

        var second = new AppSettings { FontSize = 19, AgentMonoFontFamily = "Second Mono" };
        second.ThemeColors.Accent = "#ABCDEF";
        second.AgentTheme.WorkbenchTint = "#4ADE8024";
        service.SaveSettings(second);

        var loaded = new SettingsService(configPath).ReloadSettings();
        Assert.AreEqual(19, loaded.FontSize);
        Assert.AreEqual("Second Mono", loaded.AgentMonoFontFamily);
        Assert.AreEqual("#abcdef", loaded.ThemeColors.Accent);
        Assert.AreEqual("#4ade8024", loaded.AgentTheme.WorkbenchTint);
        Assert.IsTrue(File.Exists(configPath + ".bak"));
        Assert.IsFalse(File.Exists(configPath + ".tmp"));
    }

    [TestMethod]
    public void GetSettings_InvalidActiveConfig_LoadsLastValidBackup()
    {
        using var workspace = TestWorkspace.Create(nameof(GetSettings_InvalidActiveConfig_LoadsLastValidBackup));
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        var service = new SettingsService(configPath);
        service.SaveSettings(new AppSettings { FontSize = 16, AgentMonoFontFamily = "Backup Mono" });
        File.Copy(configPath, configPath + ".bak", overwrite: true);
        File.WriteAllText(configPath, "[meta]\nkind=active-config\nschemaVersion=2\n");

        var recoveredService = new SettingsService(configPath);
        var loaded = recoveredService.GetSettings();

        Assert.AreEqual(16, loaded.FontSize);
        Assert.AreEqual("Backup Mono", loaded.AgentMonoFontFamily);
        Assert.IsNotNull(recoveredService.StartupWarning);
    }

    [TestMethod]
    public void SaveSettings_InvalidColor_IsRejectedBeforeReplacingActiveConfig()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveSettings_InvalidColor_IsRejectedBeforeReplacingActiveConfig));
        var configPath = Path.Combine(workspace.Path, "psx.ini");
        var service = new SettingsService(configPath);
        service.SaveSettings(new AppSettings { FontSize = 18 });
        var original = File.ReadAllText(configPath);
        var invalid = new AppSettings { FontSize = 20 };
        invalid.ThemeColors.Background = "red";

        Assert.ThrowsExactly<InvalidDataException>(() => service.SaveSettings(invalid));
        Assert.AreEqual(original, File.ReadAllText(configPath));
        Assert.IsFalse(File.Exists(configPath + ".tmp"));
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class ThemeServiceTests
{
    [TestMethod]
    public void ScanThemes_InjectedDirectories_KeepBuiltInAndUserThemesIsolated()
    {
        using var workspace = TestWorkspace.Create(nameof(ScanThemes_InjectedDirectories_KeepBuiltInAndUserThemesIsolated));
        var builtIn = Path.Combine(workspace.Path, "built-in");
        var user = Path.Combine(workspace.Path, "user");
        Directory.CreateDirectory(builtIn);
        Directory.CreateDirectory(user);
        CopyPreset("dark.ini", Path.Combine(builtIn, "dark.ini"));
        CopyPreset("terminal-green.ini", Path.Combine(user, "terminal-green.ini"));
        var service = new ThemeService(builtIn, user);

        var themes = service.ScanThemes();

        Assert.AreEqual(Path.GetFullPath(user), service.UserThemeDirectory);
        Assert.HasCount(2, themes);
        Assert.IsTrue(themes.Any(theme => theme.Source == ThemeSource.BuiltIn && theme.Id == "dark"));
        Assert.IsTrue(themes.Any(theme => theme.Source == ThemeSource.User));
        Assert.IsTrue(themes.All(theme => theme.FilePath.StartsWith(workspace.Path, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ScanThemes_DuplicateIdsInSameSource_MarksBothInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(ScanThemes_DuplicateIdsInSameSource_MarksBothInvalid));
        var builtIn = Path.Combine(workspace.Path, "built-in");
        var user = Path.Combine(workspace.Path, "user");
        Directory.CreateDirectory(builtIn);
        CopyPreset("dark.ini", Path.Combine(builtIn, "one.ini"));
        CopyPreset("dark.ini", Path.Combine(builtIn, "two.ini"));

        var themes = new ThemeService(builtIn, user).ScanThemes();

        Assert.HasCount(2, themes);
        Assert.IsTrue(themes.All(theme => theme.Availability == ThemeAvailability.Invalid));
        Assert.IsTrue(themes.All(theme => theme.DiagnosticSummary.Contains("Duplicate theme id", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LoadTheme_InvalidColor_ReturnsDiagnosticInsteadOfThrowing()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadTheme_InvalidColor_ReturnsDiagnosticInsteadOfThrowing));
        var themePath = Path.Combine(workspace.Path, "invalid.ini");
        var source = File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets", "dark.ini"));
        File.WriteAllText(themePath, source.Replace("background=#0c0c0d", "background=red", StringComparison.Ordinal));
        var service = new ThemeService(Path.Combine(workspace.Path, "built-in"), Path.Combine(workspace.Path, "user"));

        var result = service.LoadTheme(themePath, ThemeSource.User);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Descriptor.DiagnosticSummary, "must be #RRGGBB");
    }

    [TestMethod]
    public void ComputeFingerprint_SameAppearanceIsStableAndChangesWithColor()
    {
        using var workspace = TestWorkspace.Create(nameof(ComputeFingerprint_SameAppearanceIsStableAndChangesWithColor));
        var service = new ThemeService(Path.Combine(workspace.Path, "built-in"), Path.Combine(workspace.Path, "user"));
        var appearance = AppearanceSettings.FromSettings(new AppSettings());
        var first = service.ComputeFingerprint(appearance);
        var clone = appearance.Clone();

        Assert.AreEqual(first, service.ComputeFingerprint(clone));
        clone.ThemeColors.Accent = "#010203";
        Assert.AreNotEqual(first, service.ComputeFingerprint(clone));
    }

    private static void CopyPreset(string name, string destination)
    {
        File.Copy(Path.Combine(TestWorkspace.RepositoryRoot, "theme-presets", name), destination);
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class RuntimeLocatorTests
{
    [TestMethod]
    public void Locate_InjectedInstallDirectory_ResolvesOnlyWorkspacePaths()
    {
        using var workspace = TestWorkspace.Create(nameof(Locate_InjectedInstallDirectory_ResolvesOnlyWorkspacePaths));

        var paths = new RuntimeLocator(workspace.Path).Locate();

        Assert.AreEqual(Path.Combine(workspace.Path, "runtime"), paths.RuntimeRoot);
        Assert.AreEqual(Path.Combine(workspace.Path, "runtime", "acp-current"), paths.AcpActiveDirectory);
        Assert.AreEqual(Path.Combine(workspace.Path, "tools", "acp-seed"), paths.AcpSeedDirectory);
        Assert.IsNull(paths.PortableNodePath);
        Assert.IsNull(paths.PortableNpmCliPath);
        Assert.IsTrue(AllPaths(paths).All(path => path.StartsWith(workspace.Path, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Locate_PortableNodeAndNestedWebView2Exist_ReturnsDetectedPaths()
    {
        using var workspace = TestWorkspace.Create(nameof(Locate_PortableNodeAndNestedWebView2Exist_ReturnsDetectedPaths));
        var node = Path.Combine(workspace.Path, "tools", "node", "node.exe");
        var npm = Path.Combine(workspace.Path, "tools", "node", "node_modules", "npm", "bin", "npm-cli.js");
        var webView = Path.Combine(workspace.Path, "runtime", "webview2-fixed", "version", "msedgewebview2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(npm)!);
        Directory.CreateDirectory(Path.GetDirectoryName(webView)!);
        File.WriteAllText(node, "fake");
        File.WriteAllText(npm, "fake");
        File.WriteAllText(webView, "fake");

        var paths = new RuntimeLocator(workspace.Path).Locate();

        Assert.AreEqual(node, paths.PortableNodePath);
        Assert.AreEqual(npm, paths.PortableNpmCliPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(Path.GetDirectoryName(webView), paths.WebView2FixedRuntimePath);
    }

    private static IEnumerable<string> AllPaths(RuntimePaths paths)
    {
        yield return paths.RuntimeRoot;
        yield return paths.AcpCurrentDirectory;
        yield return paths.AcpNextDirectory;
        yield return paths.AcpActiveDirectory;
        yield return paths.AcpActivePointerFile;
        yield return paths.AcpSeedDirectory;
        yield return paths.InstallDirectory;
        yield return paths.BundledKimiDirectory;
        yield return paths.KimiCurrentDirectory;
        yield return paths.KimiNextDirectory;
        yield return paths.KimiActivePointerFile;
        yield return paths.BundledQwenDirectory;
        yield return paths.QwenCurrentDirectory;
        yield return paths.QwenNextDirectory;
        yield return paths.QwenActivePointerFile;
        yield return paths.QoderSeedDirectory;
        yield return paths.QoderCurrentDirectory;
        yield return paths.QoderNextDirectory;
        yield return paths.QoderActivePointerFile;
        yield return paths.ClineSeedDirectory;
        yield return paths.ClineCurrentDirectory;
        yield return paths.ClineNextDirectory;
        yield return paths.ClineActivePointerFile;
        yield return paths.ClineInstallingDirectory;
        yield return paths.ClineRollbackDirectory;
        yield return paths.DshSeedDirectory;
        yield return paths.DshCurrentDirectory;
        yield return paths.DshNextDirectory;
        yield return paths.DshActivePointerFile;
        yield return paths.DshInstallingDirectory;
        yield return paths.DshRollbackDirectory;
    }
}
