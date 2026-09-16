using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using PSX.Controls;

internal static class Program
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int a, int b, int c, int d);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [STAThread]
    private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args[0]);
        if (!report.Contains(Path.DirectorySeparatorChar + "TestResults" + Path.DirectorySeparatorChar)) return 64;
        Directory.CreateDirectory(report);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PSX;component/Themes/Dark.xaml") });
        var window = new PSX.MainWindow { ShowActivated = false, Left = -20000, Top = 0 };
        var passed = false;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var host = (TerminalHost)window.FindName("TerminalHostControl");
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(report, "webview"));
                await host.WebView.EnsureCoreWebView2Async(env);
                foreach (var width in new[] { 900, 1200, 1800 })
                {
                    window.Width = width;
                    window.UpdateLayout();
                    await Task.Delay(100);
                    var web = host.WebView;
                    var dpi = VisualTreeHelper.GetDpi(web);
                    var region = CreateRectRgn(0, 0, 0, 0);
                    try
                    {
                        if (GetWindowRgn(((HwndHost)web).Handle, region) == 0) throw new Exception("Missing native region");
                        var x = (int)((web.ActualWidth - 60) * dpi.DpiScaleX);
                        if (PtInRegion(region, x, (int)(20 * dpi.DpiScaleY))) throw new Exception("Caption still belongs to WebView");
                        if (!PtInRegion(region, x, (int)(80 * dpi.DpiScaleY))) throw new Exception("Content below caption was clipped");
                        if (!PtInRegion(region, 50, 20)) throw new Exception("Tab strip was clipped");
                    }
                    finally { DeleteObject(region); }
                }
                var close = (Button)window.FindName("CloseCaption");
                if (close.ActualWidth != 46 || close.ActualHeight != 48) throw new Exception("Invalid button dimensions");
                var maximize = (Button)window.FindName("MaximizeCaption");
                var center = maximize.PointToScreen(new Point(23, 20));
                var packed = new IntPtr(((int)center.X & 0xffff) | (((int)center.Y & 0xffff) << 16));
                var hwnd = new WindowInteropHelper(window).Handle;
                if (SendMessage(hwnd, 0x84, IntPtr.Zero, packed).ToInt32() != 9) throw new Exception("Snap hit test did not return HTMAXBUTTON");
                SendMessage(hwnd, 0xA1, new IntPtr(9), packed);
                SendMessage(hwnd, 0xA2, new IntPtr(9), packed);
                await Task.Delay(100);
                if (window.WindowState != WindowState.Maximized) throw new Exception("Native maximize click failed");
                window.UpdateLayout();
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) throw new Exception("Monitor lookup failed");
                var root = (FrameworkElement)window.FindName("WindowContentRoot");
                var rootTop = root.PointToScreen(new Point());
                if (Math.Abs(rootTop.Y - monitor.Work.Top) > 1 || Math.Abs(rootTop.X - monitor.Work.Left) > 1)
                    throw new Exception($"Maximized content must start at work area: {rootTop} vs {monitor.Work.Left},{monitor.Work.Top}");
                if (((Border)window.FindName("CaptionPanel")).BorderThickness != new Thickness(0))
                    throw new Exception("Caption panel must have no outline");
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                if (window.WindowState != WindowState.Normal) throw new Exception("Restore failed");
                if (root.Margin != new Thickness(0)) throw new Exception("Maximized content inset survived restore");
                ((Button)window.FindName("MinimizeCaption")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                if (window.WindowState != WindowState.Minimized) throw new Exception("Minimize failed");
                SystemCommands.RestoreWindow(window);
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                passed = true;
                File.WriteAllText(Path.Combine(report, "result.txt"), "PASS: native exclusion, full-width content, tab region, resize, button geometry, snap hit test, native maximize, restore, minimize and WPF close.");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(report, "result.txt"), ex.ToString()); }
            finally { window.Close(); app.Shutdown(passed ? 0 : 1); }
        };
        return app.Run(window);
    }
}
