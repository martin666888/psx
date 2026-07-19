using System.Windows;

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
        var window = new PSX.MainWindow();

        Assert.IsNotNull(window.FindName("TerminalHostControl"));
        Assert.IsNotNull(window.FindName("WorkspaceTabBar"));
        Assert.IsNotNull(window.FindName("ThemeButton"));
        Assert.IsNotNull(window.FindName("ThemePopup"));
        Assert.IsNotNull(window.Content);

        window.Close();
        GC.KeepAlive(application);
    }
}
