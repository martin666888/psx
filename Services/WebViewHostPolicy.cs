using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace PSX.Services;

internal enum WebViewContextMenuScope
{
    Suppressed,
    SelectedText,
    Editable
}

/// <summary>
/// Defines the deliberately small browser context-menu surface exposed by PSX.
/// The WebView is an application renderer, not a general-purpose browser: page
/// export, print, navigation and developer commands never belong in its menu.
/// </summary>
internal static class WebViewContextMenuPolicy
{
    private static readonly HashSet<string> EditableCommands = new(StringComparer.Ordinal)
    {
        "emoji",
        "undo",
        "redo",
        "cut",
        "copy",
        "paste",
        "pasteAndMatchStyle",
        "selectAll",
        "spellCheck",
        "writingDirectionMenu",
        "textDirectionDefault",
        "textDirectionLeftToRight",
        "textDirectionRightToLeft"
    };

    private static readonly HashSet<string> SelectionCommands = new(StringComparer.Ordinal)
    {
        "copy",
        "selectAll"
    };

    public static WebViewContextMenuScope GetScope(bool isEditable, CoreWebView2ContextMenuTargetKind kind)
    {
        if (isEditable)
            return WebViewContextMenuScope.Editable;

        return kind == CoreWebView2ContextMenuTargetKind.SelectedText
            ? WebViewContextMenuScope.SelectedText
            : WebViewContextMenuScope.Suppressed;
    }

    public static bool IsAllowed(WebViewContextMenuScope scope, string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
            return false;

        return scope switch
        {
            WebViewContextMenuScope.Editable => EditableCommands.Contains(commandName),
            WebViewContextMenuScope.SelectedText => SelectionCommands.Contains(commandName),
            _ => false
        };
    }
}

/// <summary>
/// Owns the browser-level policy for the single shared WebView2 instance.
/// Terminal and Agent bridges own messages; this class owns what browser
/// capabilities the embedded surface exposes to a local user.
/// </summary>
internal sealed class WebViewHostPolicy : IDisposable
{
    private readonly CoreWebView2 _coreWebView;
    private readonly string? _debugDevServerOrigin;
    private bool _disposed;

    public WebViewHostPolicy(CoreWebView2 coreWebView, string? debugDevServerOrigin = null)
    {
        _coreWebView = coreWebView;
        _debugDevServerOrigin = debugDevServerOrigin;

        ConfigureSettings(coreWebView.Settings);
        _coreWebView.ContextMenuRequested += OnContextMenuRequested;
        _coreWebView.DownloadStarting += OnDownloadStarting;
        _coreWebView.NewWindowRequested += OnNewWindowRequested;
        _coreWebView.NavigationStarting += OnNavigationStarting;
        _coreWebView.PermissionRequested += OnPermissionRequested;
    }

    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;

        // Keep the native menu machinery enabled so editable fields retain the
        // localized Windows/Edge editing commands. ContextMenuRequested below
        // reduces it to an explicit allowlist before anything is displayed.
        settings.AreDefaultContextMenusEnabled = true;
    }

    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        var scope = WebViewContextMenuPolicy.GetScope(target.IsEditable, target.Kind);
        if (scope == WebViewContextMenuScope.Suppressed)
        {
            e.Handled = true;
            return;
        }

        for (var index = e.MenuItems.Count - 1; index >= 0; index--)
        {
            var item = e.MenuItems[index];
            if (item.Kind != CoreWebView2ContextMenuItemKind.Separator
                && !WebViewContextMenuPolicy.IsAllowed(scope, item.Name))
            {
                e.MenuItems.RemoveAt(index);
            }
        }

        RemoveOrphanedSeparators(e.MenuItems);
        if (e.MenuItems.Count == 0)
            e.Handled = true;
    }

    private static void RemoveOrphanedSeparators(IList<CoreWebView2ContextMenuItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index].Kind != CoreWebView2ContextMenuItemKind.Separator)
                continue;

            var isEdge = index == 0 || index == items.Count - 1;
            var touchesSeparator = index > 0
                && items[index - 1].Kind == CoreWebView2ContextMenuItemKind.Separator;
            if (isEdge || touchesSeparator)
                items.RemoveAt(index);
        }
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // PSX has no browser-owned download surface. Future exports must be an
        // explicit host command with a reviewed data contract and save dialog.
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && WebViewNavigationPolicy.Classify(e.Uri) == WebViewNavigationTarget.External)
            OpenExternalUri(e.Uri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsDebugDevServerNavigation(e.Uri))
            return;

        var target = WebViewNavigationPolicy.Classify(e.Uri);
        if (target == WebViewNavigationTarget.Internal)
            return;

        e.Cancel = true;
        if (target == WebViewNavigationTarget.External && e.IsUserInitiated)
            OpenExternalUri(e.Uri);
    }

    private bool IsDebugDevServerNavigation(string uri)
    {
        return _debugDevServerOrigin != null
            && Uri.TryCreate(uri, UriKind.Absolute, out var navigationUri)
            && string.Equals(
                navigationUri.GetLeftPart(UriPartial.Authority),
                _debugDevServerOrigin,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind != CoreWebView2PermissionKind.ClipboardRead)
            return;

        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        e.Handled = true;
    }

    private static void OpenExternalUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine("Unable to open external URI: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine("Unable to open external URI: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _coreWebView.ContextMenuRequested -= OnContextMenuRequested;
        _coreWebView.DownloadStarting -= OnDownloadStarting;
        _coreWebView.NewWindowRequested -= OnNewWindowRequested;
        _coreWebView.NavigationStarting -= OnNavigationStarting;
        _coreWebView.PermissionRequested -= OnPermissionRequested;
    }
}
