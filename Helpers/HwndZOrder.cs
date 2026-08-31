using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PSX.Helpers;

/// <summary>
/// Two overlapping WPF <c>HwndHost</c> siblings (the shell WebView and the
/// DSH overlay) do not keep Win32 z-order in visual-tree order. The shell
/// host fills the window and lands on top after the overlay comes out of
/// <see cref="Visibility.Collapsed"/>, so DSH paints into a window the user
/// never sees. Raise the overlay HWND after it is shown.
/// </summary>
internal static class HwndZOrder
{
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    public static void BringToFront(UIElement? element)
    {
        var hwnd = TryGetHandle(element);
        if (hwnd == IntPtr.Zero)
            return;
        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
    }

    internal static IntPtr TryGetHandle(UIElement? element) =>
        element is HwndHost host ? host.Handle : IntPtr.Zero;
}
