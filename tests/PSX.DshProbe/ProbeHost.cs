using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PSX.Services;

namespace PSX.DshProbe;

/// <summary>
/// Dual-WebView2 host matching production: the shell stays on psx.local with
/// an empty column hole, and a second top-level WebView opens the token
/// Ready URL under <see cref="DshSurfaceHostPolicy"/>.
/// </summary>
internal sealed partial class ProbeHost : IDisposable
{
    private readonly string _reportDir;
    private readonly string _shellDir;
    private readonly Form _form;
    private readonly WebView2 _shellView;
    private readonly WebView2 _overlayView;
    private CoreWebView2? _shell;
    private CoreWebView2? _overlay;
    private DshSurfaceHostPolicy? _overlayPolicy;
    private Exception? _failure;

    private TaskCompletionSource<string>? _overlayMessageTcs;
    private TaskCompletionSource<(string Uri, bool IsUserInitiated)>? _newWindowTcs;
    private readonly List<string> _overlayNavigations = new();
    private readonly List<string> _shellNavigations = new();

    public string ReportDir => _reportDir;
    public string AllowedOrigin { get; }
    public Uri ReadyUrl { get; }
    public IReadOnlyList<string> OverlayNavigations => _overlayNavigations;
    public IReadOnlyList<string> ShellNavigations => _shellNavigations;
    public string? OverlayUri { get; private set; }

    public ProbeHost(string reportDir, string shellDir, Uri readyUrl)
    {
        _reportDir = reportDir;
        _shellDir = shellDir;
        ReadyUrl = readyUrl;
        AllowedOrigin = readyUrl.GetLeftPart(UriPartial.Authority);
        _form = new Form
        {
            Text = "PSX DshProbe",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1280, 800),
            ShowInTaskbar = false
        };
        _shellView = new WebView2 { Dock = DockStyle.Fill };
        _overlayView = new WebView2 { Dock = DockStyle.Fill };
        _form.Controls.Add(_shellView);
        _form.Controls.Add(_overlayView);
    }

    public async Task RunAsync(Func<ProbeHost, Task> probe)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _form.Shown += async (_, _) =>
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding");
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: Path.Combine(_reportDir, "webview2-profile"),
                    options);
                await _shellView.EnsureCoreWebView2Async(env);
                await _overlayView.EnsureCoreWebView2Async(env);
                _shell = _shellView.CoreWebView2;
                _overlay = _overlayView.CoreWebView2;
                Console.WriteLine("[probe] dual WebView2 cores ready");
                ConfigureShell();
                ConfigureOverlay();
                await probe(this);
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            System.Windows.Forms.Application.ExitThread();
            completed.TrySetResult();
        };
        _form.Show();
        System.Windows.Forms.Application.Run();
        await completed.Task.ConfigureAwait(false);
        if (_failure != null)
            throw new Exception("probe host failed: " + _failure.Message, _failure);
    }

    private void ConfigureShell()
    {
        _shell!.SetVirtualHostNameToFolderMapping(
            "psx.local", _shellDir, CoreWebView2HostResourceAccessKind.Allow);
        _shell.Settings.AreHostObjectsAllowed = false;
        _shell.Settings.AreDevToolsEnabled = false;
        _shell.Settings.IsStatusBarEnabled = false;
        _shell.NavigationStarting += OnShellNavigationStarting;
        _shell.PermissionRequested += OnPermissionRequested;
    }

    private void ConfigureOverlay()
    {
        _overlayPolicy = new DshSurfaceHostPolicy(_overlay!);
        _overlay!.WebMessageReceived += OnOverlayMessage;
        _overlay.NavigationStarting += OnOverlayNavigationStarting;
        _overlay.NewWindowRequested += OnNewWindowRequested;
        _overlay.PermissionRequested += OnPermissionRequested;
    }

    private void OnShellNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _shellNavigations.Add(e.Uri);
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && !string.Equals(uri.Host, "psx.local", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnOverlayNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _overlayNavigations.Add(e.Uri);
        OverlayUri = e.Uri;
    }

    private void OnOverlayMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        _overlayMessageTcs?.TrySetResult(e.WebMessageAsJson ?? "");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        _newWindowTcs?.TrySetResult((e.Uri, e.IsUserInitiated));
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        e.Handled = true;
    }

    public void ResetOverlayWaiters()
    {
        _overlayMessageTcs = null;
        _newWindowTcs = null;
        OverlayUri = null;
        _overlayNavigations.Clear();
        _shellNavigations.Clear();
    }

    public async Task NavigateShellAsync(string url, int timeoutMs = 15000)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            _shell!.NavigationCompleted -= handler;
            done.TrySetResult(e.IsSuccess);
        };
        _shell!.NavigationCompleted += handler;
        _shell.Navigate(url);
        var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs)) == done.Task;
        if (!finished)
            throw new TimeoutException($"shell navigation to {url} timed out");
    }

    public async Task OpenOverlayAsync(Uri readyUrl, int timeoutMs = 20000)
    {
        ResetOverlayWaiters();
        var origin = DshWebRuntimeSupervisor.ToFrameOrigin(readyUrl)
            ?? throw new InvalidOperationException("Ready URL is not a DSH loopback origin");
        await _overlayPolicy!.SetOriginAsync(origin);
        _overlay!.Navigate(readyUrl.AbsoluteUri);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            var title = ProbeUtil.Unwrap(await TryRunOverlayAsync("document.title"));
            var body = ProbeUtil.Unwrap(await TryRunOverlayAsync("document.body ? '1' : ''"));
            if (!string.IsNullOrEmpty(title) && body == "1")
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException("overlay did not finish the token exchange");
    }

    public void SetOverlayVisible(bool visible)
    {
        _overlayView.Visible = visible;
        _overlayView.Enabled = visible;
    }

    public void FocusWindow()
    {
        try
        {
            var handle = _form.Handle;
            _form.Activate();
            SetForegroundWindow(handle);
        }
        catch
        {
            // best effort
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void Dispose()
    {
        // The UI pump is gone once Application.Run returns; skip WebView2
        // teardown the same way the original probe did.
    }
}
