using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PSX.DshProbe;

/// <summary>
/// Minimal WebView2 host for the P0 probe. Mirrors the product surface:
/// https://psx.local virtual host, AreHostObjectsAllowed=false, popup/permission
/// denial, a probe-local frame-origin whitelist and download capture. Product
/// code stays untouched; Phase 3 ports these findings into WebViewHostPolicy.
/// </summary>
internal sealed partial class ProbeHost : IDisposable
{
    private readonly string _reportDir;
    private readonly string _shellDir;
    private readonly Form _form;
    private readonly WebView2 _webView;
    private CoreWebView2? _core;
    private Exception? _failure;

    private TaskCompletionSource<CoreWebView2Frame> _frameTcs = NewFrameTcs();
    private TaskCompletionSource<bool> _frameDomTcs = NewDomTcs();
    private TaskCompletionSource<string>? _frameMessageTcs;
    private TaskCompletionSource<(string Uri, bool IsUserInitiated)>? _newWindowTcs;
    private TaskCompletionSource<CoreWebView2DownloadOperation>? _downloadTcs;
    private CoreWebView2Frame? _dshFrame;
    private readonly List<string> _frameNavigations = new();
    private readonly List<string> _topNavigations = new();

    public string ReportDir => _reportDir;
    public string AllowedOrigin { get; }
    public string ExportUrl { get; }
    public IReadOnlyList<string> FrameNavigations => _frameNavigations;
    public IReadOnlyList<string> TopNavigations => _topNavigations;
    public string? DshFrameUri { get; private set; }

    public ProbeHost(string reportDir, string shellDir, Uri targetOrigin)
    {
        _reportDir = reportDir;
        _shellDir = shellDir;
        AllowedOrigin = targetOrigin.GetLeftPart(UriPartial.Authority);
        ExportUrl = targetOrigin.GetLeftPart(UriPartial.Authority) + "/api/session.export";
        _form = new Form
        {
            Text = "PSX DshProbe",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1280, 800),
            ShowInTaskbar = false
        };
        _webView = new WebView2 { Dock = DockStyle.Fill };
        _form.Controls.Add(_webView);
    }

    private static TaskCompletionSource<CoreWebView2Frame> NewFrameTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewDomTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(Func<ProbeHost, Task> probe)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _form.Shown += async (_, _) =>
        {
            try
            {
                // The offscreen probe window is treated as backgrounded by
                // Chromium, which throttles page timers/WebSockets and would
                // distort the probe results. Disable those heuristics.
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding");
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: Path.Combine(_reportDir, "webview2-profile"),
                    options);
                await _webView.EnsureCoreWebView2Async(env);
                _core = _webView.CoreWebView2;
                Console.WriteLine("[probe] WebView2 core ready");
                ConfigureCore();
                await probe(this);
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            // Exit the pump WITHOUT closing the form: form.Close() performs
            // synchronous WebView2 teardown on the UI thread, which deadlocks
            // waiting on the very pump it is blocking. The probe process exits
            // hard afterwards; the OS reclaims everything.
            Application.ExitThread();
            completed.TrySetResult();
        };
        _form.Show();
        Application.Run();
        // ConfigureAwait(false): the UI pump is gone once Application.Run
        // returns, so a WinForms-context continuation here would deadlock.
        await completed.Task.ConfigureAwait(false);
        if (_failure != null)
            throw new Exception("probe host failed: " + _failure.Message, _failure);
    }

    private void ConfigureCore()
    {
        _core!.SetVirtualHostNameToFolderMapping(
            "psx.local", _shellDir, CoreWebView2HostResourceAccessKind.Allow);
        _core.Settings.AreHostObjectsAllowed = false;
        _core.Settings.AreDevToolsEnabled = false;
        _core.Settings.IsStatusBarEnabled = false;
        _core.FrameCreated += OnFrameCreated;
        _core.FrameNavigationStarting += OnFrameNavigationStarting;
        _core.NavigationStarting += OnNavigationStarting;
        _core.NewWindowRequested += OnNewWindowRequested;
        _core.DownloadStarting += OnDownloadStarting;
        _core.PermissionRequested += OnPermissionRequested;
    }

    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        if (!string.Equals(e.Frame.Name, "dsh", StringComparison.Ordinal)) return;
        _dshFrame = e.Frame;
        e.Frame.DOMContentLoaded += (_, _) => _frameDomTcs.TrySetResult(true);
        e.Frame.WebMessageReceived += (_, message) => _frameMessageTcs?.TrySetResult(message.WebMessageAsJson ?? "");
        _frameTcs.TrySetResult(e.Frame);
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The shared args object carries no Frame reference; the exact-origin
        // whitelist itself identifies the DSH frame — every other frame
        // navigation is cancelled (the Phase 3 policy shape).
        _frameNavigations.Add(e.Uri);
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && string.Equals(uri.GetLeftPart(UriPartial.Authority), AllowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            DshFrameUri = e.Uri;
            return;
        }
        e.Cancel = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _topNavigations.Add(e.Uri);
        // Control exception: the top-level download variant must reach the
        // mock export endpoint (downloads only begin when the navigation
        // proceeds). Everything else stays locked to psx.local.
        if (string.Equals(e.Uri, ExportUrl, StringComparison.OrdinalIgnoreCase))
            return;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && !string.Equals(uri.Host, "psx.local", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // deny; the probe records the surface for the Phase 3 policy
        _newWindowTcs?.TrySetResult((e.Uri, e.IsUserInitiated));
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        var deferral = e.GetDeferral();
        e.ResultFilePath = Path.Combine(_reportDir, "export.zip");
        e.Handled = true;
        operation.StateChanged += (_, _) =>
        {
            if (operation.State is CoreWebView2DownloadState.Completed or CoreWebView2DownloadState.Interrupted)
            {
                _downloadTcs?.TrySetResult(operation);
                deferral.Complete();
            }
        };
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        e.Handled = true;
    }

    public void ResetFrameWaiters()
    {
        _frameTcs = NewFrameTcs();
        _frameDomTcs = NewDomTcs();
        _frameMessageTcs = null;
        _newWindowTcs = null;
        _downloadTcs = null;
        _dshFrame = null;
        DshFrameUri = null;
        _frameNavigations.Clear();
        _topNavigations.Clear();
    }

    public async Task NavigateAsync(string url, int timeoutMs = 15000)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            _core!.NavigationCompleted -= handler;
            done.TrySetResult(e.IsSuccess);
        };
        _core!.NavigationCompleted += handler;
        _core.Navigate(url);
        var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs)) == done.Task;
        if (!finished)
            throw new TimeoutException($"navigation to {url} timed out");
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
        // The form belongs to the UI thread, which no longer pumps messages
        // once Application.Run returns — cross-thread disposal would deadlock
        // (observed). The OS reclaims everything at process exit; the probe
        // intentionally skips explicit WebView2 teardown.
    }
}
