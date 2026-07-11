using System.IO;
using System.Diagnostics;
using System.Text.Json;
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
    private WebView2? _webView;
    private CoreWebView2? _coreWebView;
    private string _viewMode = "terminal";
    private bool _disposed;

    public event EventHandler<TerminalInputEventArgs>? InputReceived;
    public event EventHandler<TerminalResizeEventArgs>? ResizeRequested;
    public event EventHandler<TerminalTitleEventArgs>? TitleChanged;
    public event EventHandler<string>? ViewModeChanged;
    public event EventHandler? FrontendReady;

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

        // Keep browser-level shortcuts from stealing terminal shortcuts like Ctrl+Shift+C.
        _coreWebView.Settings.AreDevToolsEnabled = false;
        _coreWebView.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // Subscribe to messages from JS
        _coreWebView.WebMessageReceived += OnWebMessageReceived;

        // Navigate to the terminal page
        _coreWebView.Navigate("https://psx.local/index.html");
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
        if (string.IsNullOrEmpty(json)) return;

        TerminalMessage message;
        try
        {
            message = JsonSerializer.Deserialize<TerminalMessage>(json)!;
        }
        catch
        {
            return;
        }

        switch (message.Type)
        {
            case "input":
                if (message.Data != null && Guid.TryParse(message.SessionId, out var inputId))
                {
                    try
                    {
                        var inputData = Convert.FromBase64String(message.Data);
                        InputReceived?.Invoke(this, new TerminalInputEventArgs
                        {
                            SessionId = inputId,
                            Data = inputData
                        });
                    }
                    catch (FormatException)
                    {
                        // Malformed base64 from JS, ignore
                    }
                }
                break;

            case "resize":
                if (Guid.TryParse(message.SessionId, out var resizeId)
                    && message.Cols.HasValue && message.Rows.HasValue)
                {
                    ResizeRequested?.Invoke(this, new TerminalResizeEventArgs
                    {
                        SessionId = resizeId,
                        Cols = message.Cols.Value,
                        Rows = message.Rows.Value
                    });
                }
                break;

            case "title":
                if (Guid.TryParse(message.SessionId, out var titleId))
                {
                    TitleChanged?.Invoke(this, new TerminalTitleEventArgs
                    {
                        SessionId = titleId,
                        Title = message.Title ?? "Terminal"
                    });
                }
                break;

            case "ready":
                _ = HandleFrontendReadyAsync();
                break;
        }
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
        if (_coreWebView == null) return Task.CompletedTask;

        var json = JsonSerializer.Serialize(message, JsonOptions);

        var dispatcher = _webView?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            // Use BeginInvoke (async) instead of Invoke (sync) to avoid deadlocks
            dispatcher.BeginInvoke(() => _coreWebView.PostWebMessageAsJson(json));
        }
        else
        {
            _coreWebView.PostWebMessageAsJson(json);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_coreWebView != null)
        {
            _coreWebView.WebMessageReceived -= OnWebMessageReceived;
        }

        InputReceived = null;
        ResizeRequested = null;
        TitleChanged = null;
        ViewModeChanged = null;
        FrontendReady = null;

        _coreWebView = null;
        _webView = null;
    }
}
