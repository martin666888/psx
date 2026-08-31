using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace PSX.Helpers;

/// <summary>
/// Punches a click-through hole in an overlay <c>HwndHost</c> so a shell
/// HTML popover that lives underneath can receive input without collapsing
/// the whole overlay (which would blank the DSH frontend).
/// </summary>
internal static class HwndClipRegion
{
    private const int RgnDiff = 4;

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

    /// <summary>
    /// Intersection of <paramref name="hole"/> with the overlay, in the
    /// overlay HWND's local physical pixels. Null when they do not overlap
    /// by at least one pixel after rounding.
    /// </summary>
    internal static PixelRect? IntersectHole(
        double overlayLeft,
        double overlayTop,
        double overlayWidth,
        double overlayHeight,
        double holeLeft,
        double holeTop,
        double holeWidth,
        double holeHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        if (overlayWidth < 1 || overlayHeight < 1 || holeWidth < 1 || holeHeight < 1)
            return null;
        if (dpiScaleX <= 0 || dpiScaleY <= 0)
            return null;

        var left = Math.Max(overlayLeft, holeLeft);
        var top = Math.Max(overlayTop, holeTop);
        var right = Math.Min(overlayLeft + overlayWidth, holeLeft + holeWidth);
        var bottom = Math.Min(overlayTop + overlayHeight, holeTop + holeHeight);
        if (right - left < 0.5 || bottom - top < 0.5)
            return null;

        var widthPx = ToPx(overlayWidth, dpiScaleX);
        var heightPx = ToPx(overlayHeight, dpiScaleY);
        if (widthPx < 1 || heightPx < 1)
            return null;

        var x1 = Math.Clamp(ToPx(left - overlayLeft, dpiScaleX), 0, widthPx);
        var y1 = Math.Clamp(ToPx(top - overlayTop, dpiScaleY), 0, heightPx);
        var x2 = Math.Clamp(ToPx(right - overlayLeft, dpiScaleX), 0, widthPx);
        var y2 = Math.Clamp(ToPx(bottom - overlayTop, dpiScaleY), 0, heightPx);
        if (x2 <= x1 || y2 <= y1)
            return null;
        return new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// Ellipse diameter for <c>CreateRoundRectRgn</c>, matching a CSS corner
    /// radius. Zero means a sharp rectangle.
    /// </summary>
    internal static int CornerEllipsePx(int widthPx, int heightPx, int radiusPx)
    {
        if (widthPx < 1 || heightPx < 1 || radiusPx < 1)
            return 0;
        var radius = Math.Min(radiusPx, Math.Min(widthPx, heightPx) / 2);
        return radius * 2;
    }

    public static void Clear(UIElement? element)
    {
        var hwnd = HwndZOrder.TryGetHandle(element);
        if (hwnd == IntPtr.Zero)
            return;
        SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    public static void ApplyHole(
        UIElement? element,
        double overlayLeft,
        double overlayTop,
        double overlayWidth,
        double overlayHeight,
        double holeLeft,
        double holeTop,
        double holeWidth,
        double holeHeight,
        double holeRadius = 0)
    {
        var hwnd = HwndZOrder.TryGetHandle(element);
        if (hwnd == IntPtr.Zero || element == null)
            return;

        var dpi = VisualTreeHelper.GetDpi(element);
        var hole = IntersectHole(
            overlayLeft,
            overlayTop,
            overlayWidth,
            overlayHeight,
            holeLeft,
            holeTop,
            holeWidth,
            holeHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        if (hole is null)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        var widthPx = ToPx(overlayWidth, dpi.DpiScaleX);
        var heightPx = ToPx(overlayHeight, dpi.DpiScaleY);
        var windowRgn = CreateRectRgn(0, 0, widthPx, heightPx);
        if (windowRgn == IntPtr.Zero)
            return;

        var x1 = ToPx(holeLeft - overlayLeft, dpi.DpiScaleX);
        var y1 = ToPx(holeTop - overlayTop, dpi.DpiScaleY);
        var x2 = ToPx(holeLeft + holeWidth - overlayLeft, dpi.DpiScaleX);
        var y2 = ToPx(holeTop + holeHeight - overlayTop, dpi.DpiScaleY);
        if (x2 <= x1 || y2 <= y1)
        {
            DeleteObject(windowRgn);
            return;
        }

        var radiusPx = ToPx(holeRadius, Math.Min(dpi.DpiScaleX, dpi.DpiScaleY));
        var holeRgn = CreateHoleRegion(x1, y1, x2, y2, radiusPx);
        if (holeRgn == IntPtr.Zero)
        {
            DeleteObject(windowRgn);
            return;
        }

        CombineRgn(windowRgn, windowRgn, holeRgn, RgnDiff);
        DeleteObject(holeRgn);
        // SetWindowRgn takes ownership of windowRgn on success.
        if (SetWindowRgn(hwnd, windowRgn, true) == 0)
            DeleteObject(windowRgn);
    }

    private static IntPtr CreateHoleRegion(int x1, int y1, int x2, int y2, int radiusPx)
    {
        var ellipse = CornerEllipsePx(x2 - x1, y2 - y1, radiusPx);
        if (ellipse < 2)
            return CreateRectRgn(x1, y1, x2, y2);
        return CreateRoundRectRgn(x1, y1, x2, y2, ellipse, ellipse);
    }

    private static int ToPx(double dip, double scale) =>
        (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
}
