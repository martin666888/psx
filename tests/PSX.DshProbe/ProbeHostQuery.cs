using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace PSX.DshProbe;

internal sealed partial class ProbeHost
{
    public async Task<CoreWebView2Frame> WaitForDshFrameAsync(int timeoutMs = 15000) =>
        await _frameTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    public async Task WaitForDshDomAsync(int timeoutMs = 20000) =>
        await _frameDomTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    public async Task<CoreWebView2Frame> EnsureFrameAsync(int timeoutMs = 20000)
    {
        var frame = await WaitForDshFrameAsync(timeoutMs);
        await WaitForDshDomAsync(timeoutMs);
        return frame;
    }

    public async Task<string> RunTopAsync(string script) =>
        await _core!.ExecuteScriptAsync(script) ?? "";

    public async Task<string> RunFrameAsync(string script)
    {
        if (_dshFrame == null)
            throw new InvalidOperationException("dsh frame not ready");
        return await _dshFrame.ExecuteScriptAsync(script) ?? "";
    }

    /// <summary>Executes in the frame, tolerating transient failures while the
    /// frame is navigating; returns the raw result or an empty string.</summary>
    public async Task<string> TryRunFrameAsync(string script)
    {
        try { return await RunFrameAsync(script); }
        catch { return ""; }
    }

    /// <summary>AddScriptToExecuteOnDocumentCreatedAsync is a top-level
    /// CoreWebView2 API and runs in EVERY document of every frame, so the
    /// injected script must guard on the exact DSH origin (the Phase 3
    /// contract).</summary>
    public async Task InjectFrameScriptAsync(string script) =>
        await _core!.AddScriptToExecuteOnDocumentCreatedAsync(script);

    public async Task<string> SendCdpAsync(string method, string parametersJson) =>
        await _core!.CallDevToolsProtocolMethodAsync(method, parametersJson) ?? "";

    public void ResetFrameMessage() => _frameMessageTcs = null;

    public void ResetNewWindow() => _newWindowTcs = null;

    public void ResetDownload() => _downloadTcs = null;

    public async Task<string> WaitFrameMessageAsync(int timeoutMs = 10000)
    {
        _frameMessageTcs ??= new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return await _frameMessageTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public async Task<(string Uri, bool IsUserInitiated)> WaitNewWindowAsync(int timeoutMs = 10000)
    {
        _newWindowTcs ??= new TaskCompletionSource<(string, bool)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return await _newWindowTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public async Task<CoreWebView2DownloadOperation> WaitDownloadAsync(int timeoutMs = 15000)
    {
        _downloadTcs ??= new TaskCompletionSource<CoreWebView2DownloadOperation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return await _downloadTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    /// <summary>Polls the frame's document.title until it contains the expected
    /// text (used after a frame reload).</summary>
    public async Task<bool> WaitFrameTitleAsync(string expected, int timeoutMs = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var title = ProbeUtil.Unwrap(await RunFrameAsync("document.title"));
                if (title.Contains(expected, StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // frame mid-navigation
            }
            await Task.Delay(200);
        }
        return false;
    }
}
