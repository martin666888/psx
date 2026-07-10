using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace PSX.Controls;

/// <summary>
/// UserControl wrapping a WebView2 instance for terminal hosting.
/// </summary>
public sealed class TerminalHost : UserControl
{
    private WebView2? _webView;

    public WebView2? WebView => _webView;

    public TerminalHost()
    {
        var bgColor = ResolveBackgroundColor();

        _webView = new WebView2
        {
            DefaultBackgroundColor = bgColor
        };

        var grid = new Grid();
        grid.Children.Add(_webView);
        Content = grid;
    }

    private static System.Drawing.Color ResolveBackgroundColor()
    {
        try
        {
            if (Application.Current?.TryFindResource("WindowBackgroundBrush") is SolidColorBrush brush)
            {
                var c = brush.Color;
                return System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
            }
        }
        catch { }

        // fallback: 当前默认深色背景
        return System.Drawing.Color.FromArgb(255, 0x1D, 0x1D, 0x1A);
    }
}
