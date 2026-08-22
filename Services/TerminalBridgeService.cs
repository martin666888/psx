using System.IO;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
    private CoreWebView2? _coreWebView;
    private WebViewJsonDispatcher? _messageDispatcher;
    private WebViewHostPolicy? _hostPolicy;
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

    public TerminalBridgeService(ISettingsService settingsService, RuntimeLocator runtimeLocator)
    {
        _settingsService = settingsService;
        _runtimeLocator = runtimeLocator;
    }

    public async Task InitializeAsync(WebView2 webView)
    {
        _webView = webView;

        // Prefer the bundled Fixed Version runtime when present. This avoids
        // broken or partially installed system WebView2 runtimes on older PCs.
        var runtimePaths = _runtimeLocator.Locate();
        EnsureFixedRuntimePermissions(runtimePaths.WebView2FixedRuntimePath);
        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: runtimePaths.WebView2FixedRuntimePath);
        await webView.EnsureCoreWebView2Async(env);
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
        // Debug builds may point at a loopback Vite dev server for HMR through
        // PSX_WEB_DEV_SERVER; Release builds ignore the variable entirely.
        var navigationUrl = "https://psx.local/app/index.html";
#if DEBUG
        var devServer = Environment.GetEnvironmentVariable("PSX_WEB_DEV_SERVER");
        if (!string.IsNullOrWhiteSpace(devServer)
            && Uri.TryCreate(devServer, UriKind.Absolute, out var devUri)
            && devUri.IsLoopback
            && (devUri.Scheme == Uri.UriSchemeHttp || devUri.Scheme == Uri.UriSchemeHttps))
        {
            _debugDevServerOrigin = devUri.GetLeftPart(UriPartial.Authority);
            navigationUrl = devUri.ToString();
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

        if (_coreWebView != null)
        {
            _coreWebView.WebMessageReceived -= OnWebMessageReceived;
        }

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
        _webView = null;
    }
}
