using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private readonly IAgentSessionService? _agentSessionService;
    private readonly AcpRuntimeManager? _acpRuntime;
    private readonly RuntimePreflightService? _preflight;
    private bool _isShuttingDown;
    private bool _shutdownCompleted;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(
        MainViewModel viewModel,
        ITabManagementService tabService,
        ITerminalBridgeService bridgeService,
        IAgentBridgeService agentBridgeService,
        IAgentSessionService agentSessionService,
        AcpRuntimeManager acpRuntime,
        RuntimePreflightService preflight)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _tabService = tabService;
        _bridgeService = bridgeService;
        _agentBridgeService = agentBridgeService;
        _agentSessionService = agentSessionService;
        _acpRuntime = acpRuntime;
        _preflight = preflight;

        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    // 此方法在窗口句柄创建后、显示前触发，是调用 DWM API 的最佳时机
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, Marshal.SizeOf(darkMode));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_bridgeService == null || TerminalHostControl.WebView == null)
                return;

            // Wire ACP runtime progress to the status bar. New installations
            // only begin after the user confirms from Agent mode.
            if (_acpRuntime != null)
            {
                _acpRuntime.StatusChanged += msg => _viewModel?.SetStatus(msg);
            }

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
            if (_agentBridgeService != null)
            {
                await _agentBridgeService.InitializeAsync(TerminalHostControl.WebView);
            }

            if (_agentSessionService != null)
            {
                await _agentSessionService.PublishStateAsync();
            }

            await InitializeAcpRuntimeStatusAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败: {ex.Message}\n\n{ex.StackTrace}", "PSX 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeAcpRuntimeStatusAsync()
    {
        if (_acpRuntime == null)
            return;

        try
        {
            await _acpRuntime.TryPromoteNextToCurrentAsync().ConfigureAwait(false);
            _viewModel?.SetStatus(_acpRuntime.BuildStatusText());

            if (!_acpRuntime.GetVersionInfo().IsInstalled)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _acpRuntime.RefreshAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ACP runtime background refresh failed: " + ex);
                    _viewModel?.SetStatus(_acpRuntime.BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ACP runtime status initialization failed: " + ex);
            _viewModel?.SetStatus(_acpRuntime.BuildStatusText("更新失败，当前版本可继续使用，请下次重启尝试"));
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownCompleted)
            return;

        e.Cancel = true;
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;
        IsEnabled = false;

        (_viewModel as IDisposable)?.Dispose();
        (_tabService as IDisposable)?.Dispose();
        (_bridgeService as IDisposable)?.Dispose();
        (_agentBridgeService as IDisposable)?.Dispose();
        (_agentSessionService as IDisposable)?.Dispose();

        _shutdownCompleted = true;
        Dispatcher.BeginInvoke(Close);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Application.Current.Shutdown();
        });
    }


}
