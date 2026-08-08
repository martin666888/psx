using System.Windows;
using PSX.Services;
using PSX.Tests.Support;
using PSX.Tests.Unit;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Desktop")]
[DoNotParallelize]
public sealed class MainWindowSmokeTests
{
    [STATestMethod]
    public void XamlConstruction_CreatesTheCriticalDesktopControls()
    {
        var application = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        // App.xaml merges the theme dictionary before any window is built;
        // MainWindow.xaml resolves SoftWorkbench* styles from it via
        // StaticResource, so this bare test Application must merge it too.
        if (application.Resources.MergedDictionaries.Count == 0)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PSX;component/Themes/Dark.xaml")
            });
        }
        var window = new PSX.MainWindow();

        Assert.IsNotNull(window.FindName("TerminalHostControl"));
        Assert.IsNotNull(window.FindName("WorkspaceTabBar"));
        var tabBar = Assert.IsInstanceOfType<System.Windows.FrameworkElement>(
            window.FindName("WorkspaceTabBar"));
        Assert.IsNotNull(tabBar.FindName("NewWorkspaceButton"));
        Assert.IsNotNull(tabBar.FindName("NewWorkspacePopup"));
        Assert.IsNotNull(window.Content);

        window.Close();
        GC.KeepAlive(application);
    }
}

/// <summary>
/// Wire-contract smoke for the refactored column layout: the outgoing
/// workspace_layout payload uses columns with active tabs, and the catalog
/// reports the 3-column cap with per-workspace column placement.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class WorkspaceLayoutWireContractTests
{
    [TestMethod]
    public async Task WorkspaceLayout_PayloadUsesColumnsWithActiveTabs()
    {
        using var agents = new StubAgentWorkspaceCoordinator();
        var terminals = new RecordingTabManagementService();
        var bridge = new RecordingAgentBridgeService();
        using var manager = new WorkspaceManager(terminals, agents, bridge, new WorkspaceLayoutService());

        var first = (await manager.CreateTerminalAsync())!.Value;
        var second = (await manager.CreateTerminalAsync())!.Value;
        manager.SplitWorkspaceToNewPane(second);

        var layout = bridge.Events.Last(message => message.GetProperty("type").GetString() == "workspace_layout");
        Assert.AreEqual("column-2", layout.GetProperty("focusedColumnId").GetString(), "the split focuses the new right column");
        var columns = layout.GetProperty("columns").EnumerateArray().ToArray();
        Assert.HasCount(2, columns);
        Assert.AreEqual("column-1", columns[0].GetProperty("columnId").GetString());
        var firstColumnTabs = columns[0].GetProperty("tabs").EnumerateArray().ToArray();
        Assert.HasCount(1, firstColumnTabs);
        Assert.AreEqual(first.ToString(), firstColumnTabs[0].GetProperty("workspaceId").GetString());
        Assert.AreEqual("terminal", firstColumnTabs[0].GetProperty("kind").GetString());
        Assert.AreEqual(first.ToString(), columns[0].GetProperty("activeTabId").GetString());
        Assert.AreEqual("column-2", columns[1].GetProperty("columnId").GetString());
        Assert.AreEqual(second.ToString(), columns[1].GetProperty("activeTabId").GetString());
        Assert.AreEqual(second.ToString(), columns[1].GetProperty("tabs")[0].GetProperty("workspaceId").GetString());
    }

    [TestMethod]
    public async Task WorkspaceCatalog_CarriesColumnPlacementAndMaxColumnsIsThree()
    {
        using var agents = new StubAgentWorkspaceCoordinator();
        var terminals = new RecordingTabManagementService();
        var bridge = new RecordingAgentBridgeService();
        using var manager = new WorkspaceManager(terminals, agents, bridge, new WorkspaceLayoutService());

        await manager.CreateTerminalAsync();

        var catalog = bridge.Events.Last(message => message.GetProperty("type").GetString() == "workspace_catalog");
        Assert.AreEqual(3, catalog.GetProperty("maxColumns").GetInt32());
        Assert.IsFalse(catalog.TryGetProperty("maxPanes", out _), "maxPanes is gone");
        var workspace = catalog.GetProperty("workspaces").EnumerateArray().Single();
        Assert.AreEqual("column-1", workspace.GetProperty("columnId").GetString());
        Assert.IsTrue(workspace.GetProperty("isActiveTab").GetBoolean());
        Assert.IsFalse(workspace.TryGetProperty("canSwap", out _), "canSwap is gone");
        Assert.IsFalse(workspace.TryGetProperty("availableTargetPanes", out _), "availableTargetPanes is gone");
    }
}
