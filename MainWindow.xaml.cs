using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PSX.Models;
using PSX.Services;
using PSX.ViewModels;

namespace PSX;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private readonly MainViewModel? _viewModel;
    private readonly ITabManagementService? _tabService;
    private readonly ITerminalBridgeService? _bridgeService;
    private readonly IAgentBridgeService? _agentBridgeService;
    private readonly IAgentWorkspaceCoordinator? _agentWorkspaceCoordinator;
    private readonly IWorkspaceManager? _workspaceManager;
    private readonly IAgentRuntimeCoordinator? _agentRuntimeCoordinator;
    private readonly DshWebRuntimeSupervisor? _dshSupervisor;
    private readonly RuntimePreflightService? _preflight;
    private readonly ISettingsService? _settingsService;
    private bool _isShuttingDown;
    private bool _shutdownCompleted;
    private long _themeCatalogRevision;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(
        MainViewModel viewModel,
        ITabManagementService tabService,
        ITerminalBridgeService bridgeService,
        IAgentBridgeService agentBridgeService,
        IAgentWorkspaceCoordinator agentWorkspaceCoordinator,
        IWorkspaceManager workspaceManager,
        IAgentRuntimeCoordinator agentRuntimeCoordinator,
        DshWebRuntimeSupervisor dshSupervisor,
        RuntimePreflightService preflight,
        ISettingsService settingsService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _tabService = tabService;
        _bridgeService = bridgeService;
        _agentBridgeService = agentBridgeService;
        _agentWorkspaceCoordinator = agentWorkspaceCoordinator;
        _workspaceManager = workspaceManager;
        _agentRuntimeCoordinator = agentRuntimeCoordinator;
        _dshSupervisor = dshSupervisor;
        _preflight = preflight;
        _settingsService = settingsService;

        DataContext = _viewModel;
        _bridgeService.FrontendReady += OnFrontendReady;
        _bridgeService.ThemeActionRequested += OnThemeActionRequested;

        // Flash the taskbar button when a workspace newly needs attention
        // while this window is inactive (split-pane attention signal).
        _viewModel.AttentionAppeared += (_, _) =>
        {
            if (!IsActive)
                PSX.Helpers.NativeMethods.FlashWindow(new WindowInteropHelper(this).Handle, true);
        };

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnFrontendReady(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(PublishThemeCatalogAsync);

    private void OnThemeActionRequested(object? sender, ThemeActionEventArgs e) =>
        _ = Dispatcher.BeginInvoke(async () =>
        {
            if (_viewModel == null)
                return;
            await _viewModel.ThemePicker.HandleWebActionAsync(e.Action, e.ThemeKey);
            await PublishThemeCatalogAsync();
        });

    private Task PublishThemeCatalogAsync()
    {
        if (_viewModel == null || _agentBridgeService == null)
            return Task.CompletedTask;

        var picker = _viewModel.ThemePicker;
        return _agentBridgeService.SendEventAsync(new
        {
            type = "theme_catalog",
            revision = ++_themeCatalogRevision,
            currentLabel = picker.CurrentLabel,
            message = picker.Message,
            isMessageError = picker.IsMessageError,
            themes = picker.AllThemes.Select(item => new
            {
                key = item.Descriptor.Key,
                name = item.Name,
                source = item.Descriptor.Source.ToString().ToLowerInvariant(),
                isAvailable = item.IsAvailable,
                isCurrent = item.IsCurrent,
                isUpdated = item.IsUpdated,
                isPreview = item.IsPreview,
                diagnostic = item.DiagnosticSummary
            }).ToArray()
        });
    }

    // 此方法在窗口句柄创建后、显示前触发，是调用 DWM API 的最佳时机
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, Marshal.SizeOf(darkMode));
        if (_settingsService != null)
            AppearanceService.ApplyWpf(AppearanceSettings.FromSettings(_settingsService.GetSettings()));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_settingsService?.StartupWarning))
                _viewModel?.SetPersistentWarning(_settingsService.StartupWarning);

            if (_bridgeService == null || TerminalHostControl.WebView == null)
                return;

            // Run preflight before any WebView2-dependent service starts. The
            // big user-visible case here is Win10 systems without WebView2 —
            // the bootstrapper will install it (~30 seconds) and only then do
            // we proceed. Failures are non-fatal: we still try to bring up
            // the window so the user at least sees the error in the status
            // bar rather than a blank white screen.
            if (_preflight != null)
            {
                await RunPreflightAsync(_preflight);
            }

            await _bridgeService.InitializeAsync(TerminalHostControl.WebView);
            if (_dshSupervisor != null && _bridgeService != null)
            {
                _dshSupervisor.ReadyUrlChanged = url =>
                    _bridgeService.SetDshOrigin(DshWebRuntimeSupervisor.ToFrameOrigin(url));
            }
            if (_agentBridgeService != null)
            {
                await _agentBridgeService.InitializeAsync(TerminalHostControl.WebView);
            }

            if (_agentWorkspaceCoordinator != null)
            {
                await _agentWorkspaceCoordinator.PublishStateAsync();
            }

            await InitializeAgentRuntimeStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败: {ex.Message}\n\n{ex.StackTrace}", "PSX 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeAgentRuntimeStatusAsync()
    {
        if (_agentRuntimeCoordinator == null)
            return;

        try
        {
            // Startup only promotes locally staged updates into place. It must
            // never touch npm or the network: runtime updates are strictly
            // user-triggered from the Agent toolbar.
            await _agentRuntimeCoordinator.PrepareForStartupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ACP runtime status initialization failed: " + ex);
        }
    }

    private async Task RunPreflightAsync(RuntimePreflightService preflight)
    {
        var statuses = preflight.CheckAll();
        var webview2 = statuses.FirstOrDefault(s => s.Name == "WebView2 Runtime");
        if (webview2 == null) return;

        if (webview2.State == RuntimeComponentState.Ready)
        {
            return;
        }

        // WebView2 is required but not installed. Show a clear message and exit.
        var message =
            "未检测到 Microsoft Edge WebView2 Runtime，PSX 无法启动。\n\n" +
            "请安装 WebView2 Runtime 后重试。可联系 IT 管理员获取帮助，或从微软官网下载。\n\n" +
            "WebView2 是 PSX 界面渲染所必需的组件。";

        await Dispatcher.BeginInvoke(() =>
        {
            MessageBox.Show(message, "缺少 WebView2 Runtime", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
        });
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownCompleted)
            return;

        e.Cancel = true;
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;
        IsEnabled = false;

        _workspaceManager?.BeginShutdown();

        (_viewModel as IDisposable)?.Dispose();
        try
        {
            if (_agentWorkspaceCoordinator != null)
                await _agentWorkspaceCoordinator.ShutdownAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Agent Workspace shutdown failed: " + ex);
        }

        try
        {
            if (_tabService != null)
                await _tabService.ShutdownAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Terminal Workspace shutdown failed: " + ex);
        }
        finally
        {
            // Reap the DSH process tree after Terminal/Agent shutdown, before
            // the bridge is disposed. The Job Object (KILL_ON_JOB_CLOSE)
            // ensures the full dsh web tree dies even on a crash.
            try { _dshSupervisor?.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("DSH supervisor shutdown failed: " + ex); }

            if (_bridgeService != null)
            {
                _bridgeService.FrontendReady -= OnFrontendReady;
                _bridgeService.ThemeActionRequested -= OnThemeActionRequested;
            }
            (_agentWorkspaceCoordinator as IDisposable)?.Dispose();
            (_workspaceManager as IDisposable)?.Dispose();
            (_tabService as IDisposable)?.Dispose();
            (_agentBridgeService as IDisposable)?.Dispose();
            (_bridgeService as IDisposable)?.Dispose();

            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Application.Current.Shutdown();
        });
    }



}
