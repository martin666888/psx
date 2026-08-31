using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PSX.Services;

/// <summary>
/// Host policy for the DSH overlay WebView2. Unlike the shell WebView, this
/// instance's top-level document <em>is</em> the Harness UI: navigation is
/// locked to the current loopback origin (plus <c>about:blank</c> while idle)
/// so the launch-token exchange and Strict session cookie happen in a
/// first-party context. Downloads stay denied; exports go through the
/// document-start script's <c>chrome.webview.postMessage</c> handoff.
/// </summary>
internal sealed class DshSurfaceHostPolicy : IDisposable
{
    private readonly CoreWebView2 _coreWebView;
    private readonly object _sync = new();
    private string? _origin;
    private string? _documentScriptId;
    private long _documentScriptRevision;
    private bool _disposed;

    public DshSurfaceHostPolicy(CoreWebView2 coreWebView)
    {
        _coreWebView = coreWebView;
        ConfigureSettings(coreWebView.Settings);
        _coreWebView.ContextMenuRequested += OnContextMenuRequested;
        _coreWebView.DownloadStarting += OnDownloadStarting;
        _coreWebView.NewWindowRequested += OnNewWindowRequested;
        _coreWebView.NavigationStarting += OnNavigationStarting;
        _coreWebView.PermissionRequested += OnPermissionRequested;
    }

    public string? Origin
    {
        get { lock (_sync) return _origin; }
    }

    public async Task SetOriginAsync(string? origin)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DshSurfaceHostPolicy));

        var normalized = DshWebRuntimeSupervisor.ToFrameOrigin(
            origin != null && Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ? parsed : null);
        string? previousScript;
        lock (_sync)
        {
            _origin = normalized;
            previousScript = _documentScriptId;
            _documentScriptId = null;
            _documentScriptRevision++;
        }

        if (!string.IsNullOrEmpty(previousScript))
            TryRemoveDocumentStartScript(previousScript);

        if (normalized == null)
            return;

        var revision = _documentScriptRevision;
        var scriptId = await _coreWebView
            .AddScriptToExecuteOnDocumentCreatedAsync(BuildSurfaceScript(normalized))
            .ConfigureAwait(true);
        if (_disposed || revision != _documentScriptRevision)
        {
            TryRemoveDocumentStartScript(scriptId);
            if (_disposed)
                throw new ObjectDisposedException(nameof(DshSurfaceHostPolicy));
            return;
        }

        lock (_sync)
            _documentScriptId = scriptId;
    }

    internal static string BuildSurfaceScript(string dshOrigin)
    {
        var expected = JsonSerializer.Serialize(dshOrigin);
        return $$"""
(function(expected){
  if (String(location.origin).toLowerCase() !== String(expected).toLowerCase()) return;
  if (window.__psxDshSurfaceGuard) return;
  window.__psxDshSurfaceGuard = true;
  function post(payload){
    try {
      if (window.chrome && chrome.webview && typeof chrome.webview.postMessage === 'function')
        chrome.webview.postMessage(payload);
    } catch (e) {}
  }
  document.addEventListener('pointerdown', function(){
    post({ source: 'psx-dsh-focus' });
  }, true);
  var MARKER = '/api/session.export';
  function isExportHref(href){
    if (!href) return false;
    try { return new URL(href, location.href).pathname === MARKER; } catch(e){ return false; }
  }
  function dispatchExport(anchor){
    if (anchor.__psxExportHandled) return;
    anchor.__psxExportHandled = true;
    var filename = (anchor.download && String(anchor.download).trim()) || ('session-' + Date.now() + '.zip');
    post({ source: 'psx-dsh-export', url: anchor.href, filename: filename });
  }
  var origClick = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function(){
    if (isExportHref(this.href)) { dispatchExport(this); return; }
    return origClick.apply(this, arguments);
  };
  document.addEventListener('click', function(ev){
    var a = ev.target && ev.target.closest ? ev.target.closest('a') : null;
    if (a && isExportHref(a.href)) { ev.preventDefault(); ev.stopPropagation(); dispatchExport(a); }
  }, true);
})({{expected}});
""";
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        string? origin;
        lock (_sync) origin = _origin;
        if (origin == null)
        {
            e.Cancel = true;
            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && string.Equals(
                uri.GetLeftPart(UriPartial.Authority),
                origin,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated && WebViewNavigationPolicy.Classify(e.Uri) == WebViewNavigationTarget.External)
            OpenExternalUri(e.Uri);
    }

    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
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

        if (e.MenuItems.Count == 0)
            e.Handled = true;
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && WebViewNavigationPolicy.Classify(e.Uri) == WebViewNavigationTarget.External)
            OpenExternalUri(e.Uri);
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
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
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

    private void TryRemoveDocumentStartScript(string scriptId)
    {
        try { _coreWebView.RemoveScriptToExecuteOnDocumentCreated(scriptId); }
        catch (Exception ex)
        {
            Debug.WriteLine("Unable to remove the DSH surface script: " + ex.GetType().Name);
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
        string? scriptId;
        lock (_sync)
        {
            _documentScriptRevision++;
            scriptId = _documentScriptId;
            _documentScriptId = null;
            _origin = null;
        }
        if (!string.IsNullOrEmpty(scriptId))
            TryRemoveDocumentStartScript(scriptId);
    }
}
