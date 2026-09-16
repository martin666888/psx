using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PSX.Helpers;

namespace PSX;

public partial class MainWindow
{
    private (IntPtr Handle, double Width, double Height, double DpiX, double DpiY)? _chromeGeometry;
    private bool _chromeFallback;
    private HwndSource? _chromeSource;

    private void InitializeWindowChrome()
    {
        SizeChanged += (_, _) => UpdateWindowChrome();
        StateChanged += (_, _) => UpdateWindowChrome();
        Activated += (_, _) => PublishWindowChrome();
        Deactivated += (_, _) => PublishWindowChrome();
        DpiChanged += (_, _) => { _chromeGeometry = null; UpdateWindowChrome(); };
        TerminalHostControl.LayoutUpdated += (_, _) => UpdateWindowChrome();
        Closed += (_, _) => _chromeSource?.RemoveHook(ChromeWndProc);
        RefreshCaptionLabels();
    }

    private void RefreshCaptionLabels()
    {
        var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var labels = language switch
        {
            "zh" => new[] { "最小化", WindowState == WindowState.Maximized ? "还原" : "最大化", "关闭" },
            "ja" => new[] { "最小化", WindowState == WindowState.Maximized ? "元に戻す" : "最大化", "閉じる" },
            _ => new[] { "Minimize", WindowState == WindowState.Maximized ? "Restore" : "Maximize", "Close" }
        };
        var buttons = new[] { MinimizeCaption, MaximizeCaption, CloseCaption };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].ToolTip = labels[i];
            AutomationProperties.SetName(buttons[i], labels[i]);
        }
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M0,3 L7,3 7,10 0,10 Z M3,3 L3,0 10,0 10,7 7,7" : "M0,0 L10,0 10,10 0,10 Z");
    }

    private void UpdateWindowChrome()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized) return;
        UpdateMaximizedContentBounds();
        var web = TerminalHostControl.WebView;
        if (web == null || web.ActualWidth < 1 || web.ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(web);
        var geometry = (HwndZOrder.TryGetHandle(web), web.ActualWidth, web.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
        if (geometry.Item1 == IntPtr.Zero || _chromeGeometry == geometry) return;
        _chromeGeometry = geometry;
        RefreshCaptionLabels();
        if (!_chromeFallback && !HwndClipRegion.ApplyHole(web, 0, 0, web.ActualWidth, web.ActualHeight,
            Math.Max(0, web.ActualWidth - 138), 0, 138, 48))
        {
            _chromeFallback = true;
            HwndClipRegion.Clear(web);
            TerminalHostControl.Margin = new Thickness(0, 48, 0, 0);
            System.Diagnostics.Trace.TraceError("Shell caption region could not be applied; using separated fallback layout.");
        }
        PublishWindowChrome();
    }

    private void UpdateMaximizedContentBounds()
    {
        var margin = new Thickness(0);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Maximized && hwnd != IntPtr.Zero)
        {
            var monitor = new CaptionMonitorInfo { Size = Marshal.SizeOf<CaptionMonitorInfo>() };
            var origin = new CaptionPoint();
            if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)
                && GetClientRect(hwnd, out var client) && ClientToScreen(hwnd, ref origin))
            {
                // A maximized borderless HWND extends beyond the monitor work area by
                // its invisible resize frame. Keep both WebViews and WPF controls inside it.
                var dpi = VisualTreeHelper.GetDpi(this);
                margin = new Thickness(
                    Math.Max(0, monitor.Work.Left - origin.X) / dpi.DpiScaleX,
                    Math.Max(0, monitor.Work.Top - origin.Y) / dpi.DpiScaleY,
                    Math.Max(0, origin.X + client.Right - monitor.Work.Right) / dpi.DpiScaleX,
                    Math.Max(0, origin.Y + client.Bottom - monitor.Work.Bottom) / dpi.DpiScaleY);
            }
        }
        if (WindowContentRoot.Margin != margin) WindowContentRoot.Margin = margin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CaptionPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptionRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptionMonitorInfo { public int Size; public CaptionRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref CaptionMonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out CaptionRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref CaptionPoint point);

    private void PublishWindowChrome()
    {
        RefreshCaptionLabels();
        if (_bridgeService != null)
            _ = _bridgeService.SendWindowChromeAsync(_chromeFallback ? 0 : 138, IsActive, _chromeFallback);
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void ToggleMaximizeWindow(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
        RefreshCaptionLabels();
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private IntPtr ChromeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Native non-client maximize hit testing enables the Windows 11 snap flyout.
        if (msg == 0x0084 && IsLoaded)
        {
            var packed = lParam.ToInt64();
            var point = MaximizeCaption.PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            if (point.X >= 0 && point.Y >= 0 && point.X < MaximizeCaption.ActualWidth && point.Y < MaximizeCaption.ActualHeight)
            {
                handled = true;
                return new IntPtr(9); // HTMAXBUTTON
            }
        }
        if ((msg == 0x00A1 || msg == 0x00A2) && wParam.ToInt32() == 9)
        {
            handled = true;
            if (msg == 0x00A2) ToggleMaximize();
        }
        if (msg == 0x00A0 && wParam.ToInt32() == 9)
        {
            MaximizeCaption.SetResourceReference(Control.BackgroundProperty, "HoverBrush");
            var tracking = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = hwnd };
            TrackMouseEvent(ref tracking);
        }
        if (msg == 0x02A2) MaximizeCaption.ClearValue(Control.BackgroundProperty);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse { public uint Size; public uint Flags; public IntPtr Window; public uint HoverTime; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouse tracking);
}
