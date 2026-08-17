using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
    /// <summary>Frame-origin slot keys for the embedded web runtimes. Each
    /// runtime owns one slot; a restart atomically replaces its value without
    /// touching the other slots.</summary>
    internal const string DshFrameKind = "dsh";
    internal const string KimiWebFrameKind = "kimi_web";

    private readonly CoreWebView2 _coreWebView;
    private readonly string? _debugDevServerOrigin;
    // kind → currentOrigin snapshot (scheme://host:port, no fragment). The
    // map publishes immutable snapshots under a lock, so frame event callbacks
    // read the current reference without racing supervisor threads.
    private readonly FrameOriginMap _frameOrigins = new();
    private string? _kimiWebDocumentScriptId;
    private long _kimiWebDocumentScriptRevision;
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
        _coreWebView.FrameNavigationStarting += OnFrameNavigationStarting;
        _coreWebView.FrameCreated += OnFrameCreated;
        _coreWebView.PermissionRequested += OnPermissionRequested;
    }

    /// <summary>Compatibility wrapper for the DSH slot; equivalent to
    /// <see cref="SetFrameOrigin"/> with <see cref="DshFrameKind"/>.</summary>
    public void SetDshOrigin(string? origin) => SetFrameOrigin(DshFrameKind, origin);

    /// <summary>
    /// Set (or clear) the frame-origin slot for one embedded web runtime.
    /// A null origin clears the slot; a new value immediately revokes the
    /// previous origin for frame navigation. Must run on the UI thread
    /// (CoreWebView2 thread). Kimi's document-start hook is prepared and
    /// awaited separately before this method publishes the new origin.
    /// </summary>
    public void SetFrameOrigin(string kind, string? origin)
    {
        var normalized = NormalizeFrameOrigin(origin);
        _frameOrigins.Set(kind, normalized);

        // Stop/failed/exited revokes both navigation and the document-start
        // registration. A later start prepares a fresh exact-origin script
        // before its Ready URL can reach the frontend.
        if (normalized == null && kind == KimiWebFrameKind)
            RemoveKimiWebDocumentStartScript();
    }

    /// <summary>
    /// Install the exact-origin Kimi export/focus hook before the supervisor
    /// publishes Ready. The new registration is awaited first, then atomically
    /// replaces and removes the previous registration so restarts neither race
    /// the first iframe navigation nor accumulate stale scripts.
    /// </summary>
    public async Task PrepareFrameOriginAsync(string kind, string origin)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WebViewHostPolicy));
        if (kind != KimiWebFrameKind)
            return;

        var normalized = NormalizeFrameOrigin(origin);
        if (normalized == null)
            throw new ArgumentException("The frame origin is invalid.", nameof(origin));

        var revision = ++_kimiWebDocumentScriptRevision;
        var scriptId = await _coreWebView
            .AddScriptToExecuteOnDocumentCreatedAsync(BuildKimiWebFrameScript(normalized));
        if (_disposed || revision != _kimiWebDocumentScriptRevision)
        {
            TryRemoveDocumentStartScript(scriptId);
            if (_disposed)
                throw new ObjectDisposedException(nameof(WebViewHostPolicy));
            throw new OperationCanceledException("The Kimi frame origin changed while its script was prepared.");
        }

        var previous = _kimiWebDocumentScriptId;
        _kimiWebDocumentScriptId = scriptId;
        if (!string.IsNullOrEmpty(previous))
            TryRemoveDocumentStartScript(previous);
    }

    private void RemoveKimiWebDocumentStartScript()
    {
        _kimiWebDocumentScriptRevision++;
        var scriptId = _kimiWebDocumentScriptId;
        _kimiWebDocumentScriptId = null;
        if (!string.IsNullOrEmpty(scriptId))
            TryRemoveDocumentStartScript(scriptId);
    }

    private void TryRemoveDocumentStartScript(string scriptId)
    {
        try { _coreWebView.RemoveScriptToExecuteOnDocumentCreated(scriptId); }
        catch (Exception ex)
        {
            Debug.WriteLine("Unable to remove the Kimi Web frame script: " + ex.GetType().Name);
        }
    }

    /// <summary>Accept only an absolute http(s) loopback origin, no userinfo
    /// and no fragment; stored as scheme://host:port.</summary>
    private static string? NormalizeFrameOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsLoopback)
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Map a frame navigation source to the slot whose current origin
    /// it exactly matches, or (null, null) when it matches none.</summary>
    private (string? Kind, string? Origin) ClassifyFrameSource(string? source) =>
        _frameOrigins.Classify(source);

    private bool IsCurrentFrameOrigin(string kind, string origin) =>
        _frameOrigins.IsCurrent(kind, origin);

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
        // Top-level navigation stays locked to psx.local. DSH never navigates
        // the top document — it lives inside a frame whose origin is gated by
        // OnFrameNavigationStarting below.
        if (IsDebugDevServerNavigation(e.Uri))
            return;

        var target = WebViewNavigationPolicy.Classify(e.Uri);
        if (target == WebViewNavigationTarget.Internal)
            return;

        e.Cancel = true;
        if (target == WebViewNavigationTarget.External && e.IsUserInitiated)
            OpenExternalUri(e.Uri);
    }

    /// <summary>Frame navigation whitelist: only the current origins of the
    /// embedded web runtimes (set by their supervisors) are allowed inside a
    /// frame. Every other frame navigation is cancelled — this is the second
    /// layer after CSP <c>frame-src http://127.0.0.1:*</c>.</summary>
    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var snapshot = _frameOrigins.Snapshot;
        if (snapshot.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            var origin = uri.GetLeftPart(UriPartial.Authority);
            foreach (var slotOrigin in snapshot.Values)
            {
                if (string.Equals(origin, slotOrigin, StringComparison.OrdinalIgnoreCase))
                    return; // allowed: exact current origin of an embedded runtime
            }
        }
        e.Cancel = true;
    }

    /// <summary>Inject the embedded-runtime frame scripts: DSH gets export
    /// mediation plus a pointerdown → parent focus ping; kimi_web gets the
    /// export-fetch mediation hook (the document-start registration covers
    /// the first document; this DOMContentLoaded pass re-covers frames whose
    /// navigation raced the registration) plus the same focus ping. Injection
    /// is limited to the frame's current slot origin; other frames never
    /// receive a script, and each payload also self-gates on
    /// <c>location.origin</c>.</summary>
    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        var frame = e.Frame;
        string? kind = null;
        string? origin = null;
        frame.NavigationStarting += (_, args) => (kind, origin) = ClassifyFrameSource(args.Uri);
        frame.DOMContentLoaded += (_, _) =>
        {
            if (origin == null)
                return;
            // Second confirmation before injection: the slot must still hold
            // the origin this frame navigated to, else the runtime restarted
            // on a new port and the stale injection is abandoned.
            if (!IsCurrentFrameOrigin(kind!, origin))
                return;
            var script = kind == KimiWebFrameKind
                ? BuildKimiWebFrameScript(origin)
                : BuildDshFrameScript(origin);
            _ = frame.ExecuteScriptAsync(script);
        };
    }

    /// <summary>True when <paramref name="source"/> is exactly the current
    /// DSH loopback origin (scheme://host:port, no path).</summary>
    internal static bool IsAllowedDshFrameSource(string? source, string? dshOrigin)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dshOrigin))
            return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return false;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return string.Equals(origin, dshOrigin, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildDshFrameScript(string dshOrigin)
    {
        var expected = JsonSerializer.Serialize(dshOrigin);
        return $$"""
(function(expected){
  if (String(location.origin).toLowerCase() !== String(expected).toLowerCase()) return;
  if (window.__psxDshFrameGuard) return;
  window.__psxDshFrameGuard = true;
  document.addEventListener('pointerdown', function(){
    window.parent.postMessage({ source: 'psx-dsh-focus' }, '*');
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
    window.parent.postMessage({ source: 'psx-dsh-export', url: anchor.href, filename: filename }, '*');
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

    internal static string BuildKimiWebFrameScript(string kimiOrigin)
    {
        var expected = JsonSerializer.Serialize(kimiOrigin);
        return $$"""
(function(expected){
  if (String(location.origin).toLowerCase() !== String(expected).toLowerCase()) return;
  if (window.__psxKimiWebFrameGuard) return;
  window.__psxKimiWebFrameGuard = true;
  document.addEventListener('pointerdown', function(){
    window.parent.postMessage({ source: 'psx-kimi-web-focus' }, '*');
  }, true);
  var EXPORT_PATH = /^\/api\/v1\/sessions\/[^/]+\/export$/;
  function readUrl(input){
    return (input && typeof input === 'object' && input.url) ? input.url : input;
  }
  function readMethod(input, init){
    if (init && init.method) return String(init.method);
    if (input && input.method) return String(input.method);
    return 'GET';
  }
  function buildUrl(raw){
    if (raw == null) return null;
    try { return new URL(readUrl(raw), location.href); } catch(e){ return null; }
  }
  function dispatchExport(u){
    var m = u.pathname.match(/^\/api\/v1\/sessions\/([^/]+)\/export$/);
    window.parent.postMessage({
      source: 'psx-kimi-web-export',
      url: u.href,
      path: u.pathname,
      sessionId: m ? decodeURIComponent(m[1]) : ''
    }, '*');
  }
  var origFetch = window.fetch;
  window.fetch = function(input, init){
    var u = buildUrl(input);
    if (u
        && String(u.origin).toLowerCase() === String(location.origin).toLowerCase()
        && readMethod(input, init).toUpperCase() === 'POST'
        && EXPORT_PATH.test(u.pathname)) {
      dispatchExport(u);
      // Gently fail: the host mediates the real download and reports the
      // outcome; a TypeError keeps the page's own error handling in charge.
      return Promise.reject(new TypeError('Export is mediated by the PSX host.'));
    }
    return origFetch.apply(this, arguments);
  };
})({{expected}});
""";
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
        _coreWebView.FrameNavigationStarting -= OnFrameNavigationStarting;
        _coreWebView.FrameCreated -= OnFrameCreated;
        _coreWebView.PermissionRequested -= OnPermissionRequested;
        RemoveKimiWebDocumentStartScript();
    }
}

/// <summary>
/// kind → currentOrigin map with atomic publish semantics. Each embedded web
/// runtime owns one slot; a restart atomically replaces its value without
/// touching the other slots, and a clear revokes exactly its own slot. The
/// snapshot dictionary is immutable once published: writers swap a fresh copy
/// under the lock while frame event callbacks read the current reference, so
/// origin updates arriving from supervisor threads never race the UI thread.
/// Extracted from <see cref="WebViewHostPolicy"/> so the mapping rules are
/// unit-testable without a live CoreWebView2.
/// </summary>
internal sealed class FrameOriginMap
{
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, string> _snapshot =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The current immutable snapshot (never mutated in place).</summary>
    public IReadOnlyDictionary<string, string> Snapshot
    {
        get { lock (_lock) return _snapshot; }
    }

    /// <summary>
    /// Set (or clear) one slot. A null origin clears the slot and immediately
    /// revokes the previous origin for frame navigation; a new value swaps in
    /// a fresh snapshot. Returns true only when the snapshot actually changed.
    /// </summary>
    public bool Set(string kind, string? normalizedOrigin)
    {
        lock (_lock)
        {
            if (normalizedOrigin == null)
            {
                if (!_snapshot.ContainsKey(kind))
                    return false;
                var copy = new Dictionary<string, string>(_snapshot, StringComparer.Ordinal);
                copy.Remove(kind);
                _snapshot = copy;
                return true;
            }

            if (_snapshot.TryGetValue(kind, out var current)
                && string.Equals(current, normalizedOrigin, StringComparison.OrdinalIgnoreCase))
                return false;
            var updated = new Dictionary<string, string>(_snapshot, StringComparer.Ordinal)
            {
                [kind] = normalizedOrigin
            };
            _snapshot = updated;
            return true;
        }
    }

    /// <summary>True when <paramref name="kind"/> still holds exactly
    /// <paramref name="origin"/> (the injection re-confirmation gate: a slot
    /// that was replaced or cleared since the frame navigated abandons a
    /// stale injection).</summary>
    public bool IsCurrent(string kind, string origin)
    {
        var snapshot = Snapshot;
        return snapshot.TryGetValue(kind, out var current)
               && string.Equals(current, origin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Map a frame navigation source to the slot whose current origin
    /// it exactly matches, or (null, null) when it matches none.</summary>
    public (string? Kind, string? Origin) Classify(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return (null, null);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        foreach (var entry in Snapshot)
        {
            if (string.Equals(origin, entry.Value, StringComparison.OrdinalIgnoreCase))
                return (entry.Key, entry.Value);
        }
        return (null, null);
    }
}
