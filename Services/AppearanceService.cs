using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PSX.Models;

namespace PSX.Services;

public interface IAppearanceService
{
    Task ApplyAsync(AppearanceSettings appearance);
}

public sealed class AppearanceService : IAppearanceService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private readonly ITerminalBridgeService _bridge;

    public AppearanceService(ITerminalBridgeService bridge)
    {
        _bridge = bridge;
    }

    public async Task ApplyAsync(AppearanceSettings appearance)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            await Application.Current.Dispatcher.InvokeAsync(() => ApplyWpf(appearance));
        }
        else
        {
            ApplyWpf(appearance);
        }

        await _bridge.SendAppearanceAsync(appearance).ConfigureAwait(false);
    }

    public static void ApplyWpf(AppearanceSettings appearance)
    {
        var t = appearance.ThemeColors;
        var resources = Application.Current.Resources;
        SetBrush(resources, "WindowBackgroundBrush", t.Background);
        SetBrush(resources, "SurfaceBrush", t.Surface);
        SetBrush(resources, "SurfaceRaisedBrush", t.SurfaceRaised);
        SetBrush(resources, "SurfaceMutedBrush", t.SurfaceMuted);
        SetBrush(resources, "HoverBrush", t.Hover);
        SetBrush(resources, "BorderBrush", t.Border);
        SetBrush(resources, "BorderStrongBrush", t.BorderStrong);
        SetBrush(resources, "TextPrimaryBrush", t.Text);
        SetBrush(resources, "TextSecondaryBrush", t.TextMuted);
        SetBrush(resources, "TextDimBrush", t.TextDim);
        SetBrush(resources, "AccentBrush", t.Accent);
        SetBrush(resources, "AccentHoverBrush", t.AccentHover);
        SetBrush(resources, "ErrorBrush", t.Error);
        SetBrush(resources, "ErrorBgBrush", t.ErrorBg);
        SetBrush(resources, "WarningBrush", t.Warning);
        SetBrush(resources, "WarningBgBrush", t.WarningBg);
        SetBrush(resources, "ScrollbarBrush", t.Scrollbar);
        SetBrush(resources, "ScrollbarHoverBrush", t.ScrollbarHover);
        ApplyTitleBar(t.Background);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string cssColor)
    {
        var wpfColor = cssColor.Length == 9
            ? $"#{cssColor[7..9]}{cssColor[1..7]}"
            : cssColor;
        var color = (Color)ColorConverter.ConvertFromString(wpfColor);
        resources[key] = new SolidColorBrush(color);
    }

    private static void ApplyTitleBar(string background)
    {
        var window = Application.Current.MainWindow;
        if (window == null || background.Length < 7)
            return;

        if (!byte.TryParse(background.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(background.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(background.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
            return;

        var luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
        var darkMode = luminance < 140 ? 1 : 0;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
