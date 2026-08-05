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
        Assert.IsNotNull(window.FindName("ThemeButton"));
        Assert.IsNotNull(window.FindName("ThemePopup"));
        var tabBar = Assert.IsInstanceOfType<System.Windows.FrameworkElement>(
            window.FindName("WorkspaceTabBar"));
        Assert.IsNotNull(tabBar.FindName("NewWorkspaceButton"));
        Assert.IsNotNull(tabBar.FindName("NewWorkspacePopup"));
        Assert.IsNotNull(window.Content);

        window.Close();
        GC.KeepAlive(application);
    }
}
