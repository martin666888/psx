using System.IO;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PSX.Helpers;
using PSX.Models;

namespace PSX.Services;

public sealed class TerminalBridgeService : ITerminalBridgeService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ISettingsService _settingsService;
    private readonly RuntimeLocator _runtimeLocator;
    private readonly ConcurrentDictionary<Guid, byte> _terminalSessionIds = new();
    private WebView2? _webView;
    private WebView2? _dshSurface;
    private CoreWebView2? _coreWebView;
    private CoreWebView2? _dshCore;
    private CoreWebView2Environment? _environment;
    private WebViewJsonDispatcher? _messageDispatcher;
    private WebViewHostPolicy? _hostPolicy;
    private DshSurfaceHostPolicy? _dshPolicy;
    private Uri? _dshReadyUrl;
    private Uri? _dshNavigatedUrl;
    private long _dshReadyRevision;
    private DshSurfaceBoundsEventArgs? _dshBounds;
    private bool _dshOverlayZOrderHooked;
    private string _viewMode = "terminal";
    private bool _disposed;
#if DEBUG
    // Loopback-only Vite dev server origin accepted for this session; never
    // set in Release builds (the env variable is not even read there).
    private string? _debugDevServerOrigin;
#endif

    public event EventHandler<TerminalInputEventArgs>? InputReceived;
    public event EventHandler<TerminalResizeEventArgs>? ResizeRequested;
    public event EventHandler<TerminalTitleEventArgs>? TitleChanged;
    public event EventHandler<string>? ViewModeChanged;
    public event EventHandler? FrontendReady;
    public event EventHandler<string>? PaneFocusRequested;
    public event EventHandler<PaneRatiosEventArgs>? PaneRatiosRequested;
    public event EventHandler<PaneMoveEventArgs>? PaneMoveRequested;
    public event EventHandler<WorkspaceLayoutIntentEventArgs>? WorkspaceLayoutIntentRequested;
    public event EventHandler<WorkspaceCreateEventArgs>? WorkspaceCreateRequested;
    public event EventHandler<DshCommandEventArgs>? DshCommandRequested;
    public event EventHandler<KimiWebCommandEventArgs>? KimiWebCommandRequested;
    public event EventHandler<DshExportEventArgs>? DshExportRequested;
    public event EventHandler<KimiWebExportEventArgs>? KimiWebExportRequested;
    public event EventHandler<ThemeActionEventArgs>? ThemeActionRequested;
    public event EventHandler<AppSettingsCommandEventArgs>? AppSettingsCommandRequested;

    /// <summary>Resolves the current display locale for the bootstrap URL;
    /// deferred so the settings coordinator is only pulled when navigation
    /// actually happens.</summary>
    private readonly Func<string>? _resolvedLocaleProvider;

    public TerminalBridgeService(ISettingsService settingsService, RuntimeLocator runtimeLocator)
        : this(settingsService, runtimeLocator, null)
    {
    }

    public TerminalBridgeService(
        ISettingsService settingsService,
        RuntimeLocator runtimeLocator,
        Func<string>? resolvedLocaleProvider)
    {
        _settingsService = settingsService;
        _runtimeLocator = runtimeLocator;
        _resolvedLocaleProvider = resolvedLocaleProvider;
    }

    public async Task InitializeAsync(WebView2 webView)
    {
        _webView = webView;

        // Prefer the bundled Fixed Version runtime when present. This avoids
        // broken or partially installed system WebView2 runtimes on older PCs.
        var runtimePaths = _runtimeLocator.Locate();
        EnsureFixedRuntimePermissions(runtimePaths.WebView2FixedRuntimePath);
        _environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: runtimePaths.WebView2FixedRuntimePath);
        await webView.EnsureCoreWebView2Async(_environment);
        _coreWebView = webView.CoreWebView2;
        _messageDispatcher?.Dispose();
        _messageDispatcher = new WebViewJsonDispatcher(
            webView.Dispatcher,
            _coreWebView.PostWebMessageAsJson);

        // Set up virtual host mapping for wwwroot
        var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _coreWebView.SetVirtualHostNameToFolderMapping(
            "psx.local",
            wwwrootPath,
            CoreWebView2HostResourceAccessKind.Allow);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var attachmentsPath = Path.Combine(userProfile, ".psx", "agent", "attachments");
        Directory.CreateDirectory(attachmentsPath);
        _coreWebView.SetVirtualHostNameToFolderMapping(
            "psx-attachments.local",
            attachmentsPath,
            CoreWebView2HostResourceAccessKind.Allow);

        // Navigate to the packaged frontend under the /app/ virtual-host path.
        // The resolved locale rides on the query so i18next can initialize
        // synchronously before any controller renders. Language switches later
        // broadcast over the bridge — the shell never re-navigates. Debug
        // builds may point at a loopback Vite dev server for HMR through
        // PSX_WEB_DEV_SERVER; Release builds ignore the variable entirely.
        var locale = _resolvedLocaleProvider?.Invoke();
        var navigationUrl = string.IsNullOrWhiteSpace(locale)
            ? "https://psx.local/app/index.html"
            : $"https://psx.local/app/index.html?locale={Uri.EscapeDataString(locale)}";
#if DEBUG
        var devServer = Environment.GetEnvironmentVariable("PSX_WEB_DEV_SERVER");
        if (!string.IsNullOrWhiteSpace(devServer)
            && Uri.TryCreate(devServer, UriKind.Absolute, out var devUri)
            && devUri.IsLoopback
            && (devUri.Scheme == Uri.UriSchemeHttp || devUri.Scheme == Uri.UriSchemeHttps))
        {
            _debugDevServerOrigin = devUri.GetLeftPart(UriPartial.Authority);
            navigationUrl = devUri.ToString();
            if (!string.IsNullOrWhiteSpace(locale))
                navigationUrl += (devUri.Query.Length > 0 ? "&" : "?") + $"locale={Uri.EscapeDataString(locale)}";
        }
#endif
        _hostPolicy?.Dispose();
        _hostPolicy = new WebViewHostPolicy(
            _coreWebView,
#if DEBUG
            _debugDevServerOrigin
#else
            null
#endif
        );
        webView.ZoomFactor = 1.0;
        _coreWebView.WebMessageReceived += OnWebMessageReceived;
        _coreWebView.Navigate(navigationUrl);
    }

    /// <summary>
    /// Second WebView2 for the DSH column: top-level navigation to the
    /// token-bearing Ready URL so the Strict session cookie is first-party.
    /// Shares the shell's WebView2 environment. Must run after
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public async Task InitializeDshSurfaceAsync(WebView2 webView)
    {
        if (_environment == null)
            throw new InvalidOperationException("The shell WebView must be initialized first.");

        _dshSurface = webView;
        await webView.EnsureCoreWebView2Async(_environment);
        _dshCore = webView.CoreWebView2;
        _dshPolicy?.Dispose();
        _dshPolicy = new DshSurfaceHostPolicy(_dshCore);
        webView.ZoomFactor = 1.0;
        _dshCore.WebMessageReceived += OnDshSurfaceMessage;
        _dshCore.Navigate("about:blank");
    }

    /// <summary>
    /// Full Ready URL including the one-time <c>?token=</c> query. The overlay
    /// WebView is the only document that opens it. Null clears the surface.
    /// Must run on the UI thread (CoreWebView2 thread).
    /// </summary>
    public void SetDshReadyUrl(Uri? url)
    {
        if (_dshSurface == null)
            return;
        if (_dshSurface.Dispatcher.CheckAccess())
        {
            _ = ApplyDshReadyUrlAsync(url);
            return;
        }

        _ = _dshSurface.Dispatcher.InvokeAsync(() => ApplyDshReadyUrlAsync(url));
    }

    public void ApplyDshSurfaceBounds(DshSurfaceBoundsEventArgs bounds)
    {
        if (_dshSurface == null)
            return;
        if (_dshSurface.Dispatcher.CheckAccess())
        {
            ApplyDshSurfaceBoundsCore(bounds);
            return;
        }

        _ = _dshSurface.Dispatcher.InvokeAsync(() => ApplyDshSurfaceBoundsCore(bounds));
    }

    private async Task ApplyDshReadyUrlAsync(Uri? url)
    {
        // Capture the generation before any await. A later SetDshReadyUrl
        // (fast restart) increments this; resuming an older call must not
        // navigate the overlay to a consumed token.
        var revision = ++_dshReadyRevision;
        _dshReadyUrl = url;
        var origin = DshWebRuntimeSupervisor.ToFrameOrigin(url);
        if (_dshPolicy != null)
            await _dshPolicy.SetOriginAsync(origin);
        if (revision != _dshReadyRevision)
            return;

        if (url == null || origin == null)
        {
            _dshNavigatedUrl = null;
            if (_dshCore != null)
                _dshCore.Navigate("about:blank");
            ApplyDshSurfaceBoundsCore(_dshBounds ?? new DshSurfaceBoundsEventArgs { Visible = false });
            return;
        }

        if (_dshNavigatedUrl != null
            && Uri.Compare(
                _dshNavigatedUrl,
                url,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.Ordinal) == 0)
        {
            ApplyDshSurfaceBoundsCore(_dshBounds ?? new DshSurfaceBoundsEventArgs { Visible = false });
            return;
        }

        // First navigation of this generation consumes the launch token.
        _dshNavigatedUrl = url;
        _dshCore?.Navigate(url.AbsoluteUri);
        ApplyDshSurfaceBoundsCore(_dshBounds ?? new DshSurfaceBoundsEventArgs { Visible = false });
    }

    private void ApplyDshSurfaceBoundsCore(DshSurfaceBoundsEventArgs bounds)
    {
        _dshBounds = bounds;
        var surface = _dshSurface;
        if (surface == null)
            return;

        var show = bounds.Visible
            && _dshReadyUrl != null
            && bounds.Width >= 1
            && bounds.Height >= 1;
        if (!show)
        {
            UnhookDshOverlayZOrder(surface);
            HwndClipRegion.Clear(surface);
            surface.Visibility = Visibility.Collapsed;
            surface.IsHitTestVisible = false;
            return;
        }

        surface.Margin = new Thickness(bounds.Left, bounds.Top, 0, 0);
        surface.Width = bounds.Width;
        surface.Height = bounds.Height;
        surface.Visibility = Visibility.Visible;
        surface.IsHitTestVisible = true;
        // The shell WebView's HWND fills the window and sits above this
        // overlay after Collapsed→Visible. Raise after the layout that
        // materializes the host window, otherwise DSH paints underneath.
        RaiseAndClipDshOverlay(surface);
        HookDshOverlayZOrder(surface);
        _ = surface.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RaiseAndClipDshOverlay(surface));
        _ = surface.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => RaiseAndClipDshOverlay(surface));
    }

    private void RaiseAndClipDshOverlay(WebView2? surface)
    {
        if (surface == null || surface.Visibility != Visibility.Visible)
            return;
        HwndZOrder.BringToFront(surface);
        ApplyDshOverlayClip(surface);
    }

    private void ApplyDshOverlayClip(WebView2 surface)
    {
        var bounds = _dshBounds;
        if (bounds is { Visible: true, Exclude: { Width: > 0, Height: > 0 } exclude })
        {
            HwndClipRegion.ApplyHole(
                surface,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                exclude.Left,
                exclude.Top,
                exclude.Width,
                exclude.Height,
                exclude.Radius);
            return;
        }

        HwndClipRegion.Clear(surface);
    }

    private void HookDshOverlayZOrder(WebView2 surface)
    {
        if (_dshOverlayZOrderHooked)
            return;
        surface.LayoutUpdated += OnDshOverlayLayoutUpdated;
        _dshOverlayZOrderHooked = true;
    }

    private void UnhookDshOverlayZOrder(WebView2 surface)
    {
        if (!_dshOverlayZOrderHooked)
            return;
        surface.LayoutUpdated -= OnDshOverlayLayoutUpdated;
        _dshOverlayZOrderHooked = false;
    }

    private void OnDshOverlayLayoutUpdated(object? sender, EventArgs e) =>
        RaiseAndClipDshOverlay(_dshSurface);

    private void OnDshSurfaceMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        var source = root.TryGetProperty("source", out var sourceElement)
            && sourceElement.ValueKind == JsonValueKind.String
            ? sourceElement.GetString()
            : null;
        if (source == "psx-dsh-focus")
        {
            var columnId = _dshBounds?.ColumnId;
            if (!string.IsNullOrWhiteSpace(columnId))
                PaneFocusRequested?.Invoke(this, columnId);
            return;
        }

        if (source != "psx-dsh-export")
            return;
        var url = root.TryGetProperty("url", out var urlElement)
            && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        var filename = root.TryGetProperty("filename", out var filenameElement)
            && filenameElement.ValueKind == JsonValueKind.String
            ? filenameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(filename))
            return;
        DshExportRequested?.Invoke(this, new DshExportEventArgs { Url = url, Filename = filename });
    }

    private static void EnsureFixedRuntimePermissions(string? fixedRuntimePath)
    {
        if (fixedRuntimePath == null)
            return;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10) ||
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        GrantReadExecute(fixedRuntimePath, "*S-1-15-2-2:(OI)(CI)(RX)");
        GrantReadExecute(fixedRuntimePath, "*S-1-15-2-1:(OI)(CI)(RX)");
    }

    private static void GrantReadExecute(string path, string sidRule)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "icacls",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("/grant");
            startInfo.ArgumentList.Add(sidRule);

            using var process = Process.Start(startInfo);
            if (process != null && !process.WaitForExit(5000))
                process.Kill();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Unable to grant WebView2 fixed runtime ACLs: " + ex);
        }
    }

    public Task CreateTerminalAsync(Guid sessionId)
    {
        _terminalSessionIds[sessionId] = 0;
        return SendMessageToJs(new TerminalMessage
        {
            Type = "create",
            SessionId = sessionId.ToString()
        });
    }

    public Task SendOutputAsync(Guid sessionId, string base64Data)
    {
        return SendMessageToJs(new TerminalMessage
        {
            Type = "output",
            SessionId = sessionId.ToString(),
            Data = base64Data
        });
    }

    public Task SwitchTerminalAsync(Guid sessionId)
    {
        return SendMessageToJs(new TerminalMessage
        {
            Type = "switch",
            SessionId = sessionId.ToString()
        });
    }

    public Task CloseTerminalAsync(Guid sessionId)
    {
        _terminalSessionIds.TryRemove(sessionId, out _);
        return SendMessageToJs(new TerminalMessage
        {
            Type = "close",
            SessionId = sessionId.ToString()
        });
    }

    public Task ResizeTerminalAsync(Guid sessionId, int cols, int rows)
    {
        return SendMessageToJs(new TerminalMessage
        {
            Type = "resize",
            SessionId = sessionId.ToString(),
            Cols = cols,
            Rows = rows
        });
    }

    public Task SetViewModeAsync(string mode)
    {
        var nextMode = string.Equals(mode, "agent", StringComparison.OrdinalIgnoreCase)
            ? "agent"
            : "terminal";

        _viewMode = nextMode;
        ViewModeChanged?.Invoke(this, _viewMode);

        return SendMessageToJs(new
        {
            type = "view_mode",
            mode = _viewMode
        });
    }

    public Task SendAppearanceAsync(AppearanceSettings appearance)
    {
        return SendMessageToJs(new
        {
            type = "appearance_settings",
            settings = new
            {
                fontSize = appearance.TerminalFontSize,
                fontFamily = appearance.TerminalFontFamily,
                agentFontSize = appearance.AgentFontSize,
                agentFontFamily = appearance.AgentFontFamily,
                agentMonoFontFamily = appearance.AgentMonoFontFamily,
                themeColors = appearance.ThemeColors,
                agentThemeColors = appearance.AgentTheme,
                terminalColors = appearance.TerminalColors
            }
        });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        if (!TerminalBridgeMessageParser.TryParse(json, out var message) || message == null)
            return;

        switch (message.Kind)
        {
            case TerminalBridgeMessageKind.Input:
                InputReceived?.Invoke(this, message.Input!);
                break;
            case TerminalBridgeMessageKind.Resize:
                ResizeRequested?.Invoke(this, message.Resize!);
                break;
            case TerminalBridgeMessageKind.Title:
                TitleChanged?.Invoke(this, message.Title!);
                break;
            case TerminalBridgeMessageKind.PasteRequest:
                HandlePasteRequest(message.PasteRequest!);
                break;
            case TerminalBridgeMessageKind.Ready:
                _ = HandleFrontendReadyAsync();
                break;
            case TerminalBridgeMessageKind.PaneFocus:
                PaneFocusRequested?.Invoke(this, message.PaneId!);
                break;
            case TerminalBridgeMessageKind.PaneRatiosCommit:
                PaneRatiosRequested?.Invoke(this, message.PaneRatios!);
                break;
            case TerminalBridgeMessageKind.PaneMove:
                PaneMoveRequested?.Invoke(this, new PaneMoveEventArgs
                {
                    WorkspaceId = message.WorkspaceId!.Value,
                    PaneId = message.PaneId!
                });
                break;
            case TerminalBridgeMessageKind.WorkspaceLayoutIntent:
                WorkspaceLayoutIntentRequested?.Invoke(this, message.WorkspaceIntent!);
                break;
            case TerminalBridgeMessageKind.WorkspaceCreate:
                WorkspaceCreateRequested?.Invoke(this, message.WorkspaceCreate!);
                break;
            case TerminalBridgeMessageKind.DshCommand:
                DshCommandRequested?.Invoke(this, message.DshCommand!);
                break;
            case TerminalBridgeMessageKind.KimiWebCommand:
                KimiWebCommandRequested?.Invoke(this, message.KimiWebCommand!);
                break;
            case TerminalBridgeMessageKind.DshExport:
                DshExportRequested?.Invoke(this, message.DshExport!);
                break;
            case TerminalBridgeMessageKind.DshSurfaceBounds:
                ApplyDshSurfaceBounds(message.DshSurfaceBounds!);
                break;
            case TerminalBridgeMessageKind.KimiWebExport:
                KimiWebExportRequested?.Invoke(this, message.KimiWebExport!);
                break;
            case TerminalBridgeMessageKind.ThemeAction:
                ThemeActionRequested?.Invoke(this, message.ThemeAction!);
                break;
            case TerminalBridgeMessageKind.AppSettingsCommand:
                AppSettingsCommandRequested?.Invoke(this, message.AppSettingsCommand!);
                break;
        }
    }

    private void HandlePasteRequest(TerminalPasteRequest request)
    {
        if (!_terminalSessionIds.ContainsKey(request.SessionId))
        {
            _ = SendPasteResponseAsync(request, ok: false, text: "");
            return;
        }

        try
        {
            var text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : "";
            _ = SendPasteResponseAsync(request, ok: true, text);
        }
        catch (COMException)
        {
            _ = SendPasteResponseAsync(request, ok: false, text: "");
        }
    }

    private Task SendPasteResponseAsync(TerminalPasteRequest request, bool ok, string text)
    {
        return SendMessageToJs(new TerminalMessage
        {
            Type = "paste_response",
            SessionId = request.SessionId.ToString(),
            RequestId = request.RequestId.ToString(),
            Ok = ok,
            Text = text
        });
    }

    private async Task HandleFrontendReadyAsync()
    {
        await SendMessageToJs(new TerminalMessage
        {
            Type = "settings",
            Settings = TerminalOptions.FromSettings(_settingsService.GetSettings())
        });

        await SetViewModeAsync(_viewMode);

        FrontendReady?.Invoke(this, EventArgs.Empty);
    }

    private Task SendMessageToJs(object message)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        return _messageDispatcher?.SendAsync(json) ?? Task.CompletedTask;
    }

    /// <summary>Set (or clear) the frame-origin slot for one embedded web
    /// runtime. Marshals to the UI thread when needed: CoreWebView2 APIs
    /// (including the document-start script registration for kimi_web) are
    /// thread-affine, while supervisors fire their origin callbacks from
    /// worker threads.</summary>
    public void SetFrameOrigin(string kind, string? origin)
    {
        var policy = _hostPolicy;
        if (policy == null)
            return;
        if (_webView == null || _webView.Dispatcher.CheckAccess())
        {
            policy.SetFrameOrigin(kind, origin);
            return;
        }
        try
        {
            _webView.Dispatcher.InvokeAsync(() => policy.SetFrameOrigin(kind, origin));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("SetFrameOrigin marshal failed: " + ex.Message);
        }
    }

    public Task PrepareFrameOriginAsync(string kind, string origin)
    {
        var policy = _hostPolicy;
        if (policy == null || _webView == null)
            return Task.CompletedTask;
        if (_webView.Dispatcher.CheckAccess())
            return policy.PrepareFrameOriginAsync(kind, origin);

        try
        {
            return _webView.Dispatcher
                .InvokeAsync(() => policy.PrepareFrameOriginAsync(kind, origin))
                .Task.Unwrap();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PrepareFrameOrigin marshal failed: " + ex.GetType().Name);
            return Task.FromException(ex);
        }
    }

    /// <summary>Compatibility wrapper for the DSH slot (SetFrameOrigin with
    /// kind "dsh").</summary>
    public void SetDshOrigin(string? origin) => SetFrameOrigin(WebViewHostPolicy.DshFrameKind, origin);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messageDispatcher?.Dispose();
        _messageDispatcher = null;
        _hostPolicy?.Dispose();
        _hostPolicy = null;
        _dshPolicy?.Dispose();
        _dshPolicy = null;

        if (_coreWebView != null)
        {
            _coreWebView.WebMessageReceived -= OnWebMessageReceived;
        }
        if (_dshCore != null)
        {
            _dshCore.WebMessageReceived -= OnDshSurfaceMessage;
        }

        if (_dshSurface != null)
            UnhookDshOverlayZOrder(_dshSurface);

        InputReceived = null;
        ResizeRequested = null;
        TitleChanged = null;
        ViewModeChanged = null;
        FrontendReady = null;
        WorkspaceLayoutIntentRequested = null;
        WorkspaceCreateRequested = null;
        ThemeActionRequested = null;
        AppSettingsCommandRequested = null;
        _terminalSessionIds.Clear();

        _coreWebView = null;
        _dshCore = null;
        _dshSurface = null;
        _environment = null;
        _dshReadyUrl = null;
        _dshNavigatedUrl = null;
        _dshBounds = null;
        _webView = null;
    }
}
