using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PSX.Controls;

namespace PSX.Services;

/// <summary>Opt-in packaged startup probe. Runs before user services are constructed.</summary>
internal static class PackageSmokeProbe
{
    public static async Task RunAsync(Application application, string reportDirectory)
    {
        var report = Path.GetFullPath(reportDirectory);
        if (!report.Split(Path.DirectorySeparatorChar).Contains("TestResults", StringComparer.OrdinalIgnoreCase))
        {
            application.Shutdown(64);
            return;
        }
        Directory.CreateDirectory(report);
        MainWindow? window = null;
        var exitCode = 1;
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var layout = PackageLayout.Current;
            if (!File.Exists(layout.ConfigPath)) throw new IOException("Packaged configuration missing.");
            if (!PiAcpRuntime.ValidateRoot(Path.Combine(layout.ResourceRoot, "tools", "pi"))) throw new IOException("Bundled Pi missing.");
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = 0 };
            window.Show();
            var web = ((TerminalHost)window.FindName("TerminalHostControl")).WebView!;
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(report, "webview"));
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("psx.local", Path.Combine(layout.ResourceRoot, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
            using var policy = new WebViewHostPolicy(web.CoreWebView2);
            var ready = new TaskCompletionSource();
            web.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                using var message = JsonDocument.Parse(args.TryGetWebMessageAsString());
                if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ready") ready.TrySetResult();
            };
            web.CoreWebView2.Navigate("https://psx.local/app/index.html");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            File.WriteAllText(Path.Combine(report, "result.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                layout.IsCompact,
                layout.PackageRoot,
                layout.ResourceRoot,
                layout.ConfigPath,
                processPath = Environment.ProcessPath
            }));
            web.Dispose();
            exitCode = 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(report, "error.txt"), ex.ToString()); }
        finally { window?.Close(); application.Shutdown(exitCode); }
    }
}
