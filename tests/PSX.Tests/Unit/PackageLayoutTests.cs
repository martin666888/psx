using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class PackageLayoutTests
{
    [TestMethod]
    public void CompactLayout_KeepsConfigAtRootAndResourcesInsideApp()
    {
        using var workspace = TestWorkspace.Create(nameof(PackageLayoutTests));
        var app = Path.Combine(workspace.Path, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "psx-compact-layout.json"), "{\"version\":1}");
        var layout = PackageLayout.Resolve(app);
        Assert.IsTrue(layout.IsCompact);
        Assert.AreEqual(Path.Combine(workspace.Path, "psx.ini"), layout.ConfigPath);
        Assert.AreEqual(Path.Combine(app, "state", "webview2"), layout.WebViewDataDirectory);
        Assert.AreEqual(app, layout.ResourceRoot);
    }

    [TestMethod]
    public void DirectoryNamedApp_WithoutMarkerRetainsLegacyLayout()
    {
        using var workspace = TestWorkspace.Create(nameof(PackageLayoutTests));
        var app = Path.Combine(workspace.Path, "app");
        Directory.CreateDirectory(app);
        var layout = PackageLayout.Resolve(app);
        Assert.IsFalse(layout.IsCompact);
        Assert.AreEqual(Path.Combine(app, "psx.ini"), layout.ConfigPath);
        Assert.IsNull(layout.WebViewDataDirectory);
    }

    [TestMethod]
    public void CompactSettings_SaveKeepsBackupAndTemporaryFilesInsideApp()
    {
        using var workspace = TestWorkspace.Create(nameof(PackageLayoutTests));
        var state = Path.Combine(workspace.Path, "app", "state", "settings");
        var config = Path.Combine(workspace.Path, "psx.ini");
        var service = new SettingsService(config, state);
        var settings = service.GetSettings();
        service.SaveSettings(settings);
        service.SaveSettings(settings);
        Assert.IsTrue(File.Exists(config));
        Assert.IsTrue(File.Exists(Path.Combine(state, "psx.ini.bak")));
        Assert.IsFalse(File.Exists(config + ".bak"));
        Assert.IsFalse(File.Exists(config + ".tmp"));
    }
}
