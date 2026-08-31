using System.Diagnostics;

namespace PSX.DshProbe;

internal sealed partial class ProbeHost
{
    public async Task<string> RunShellAsync(string script) =>
        await _shell!.ExecuteScriptAsync(script) ?? "";

    public async Task<string> RunOverlayAsync(string script)
    {
        if (_overlay == null)
            throw new InvalidOperationException("overlay WebView is not ready");
        return await _overlay.ExecuteScriptAsync(script) ?? "";
    }

    public async Task<string> TryRunOverlayAsync(string script)
    {
        try { return await RunOverlayAsync(script); }
        catch { return ""; }
    }

    public void ResetOverlayMessage() => _overlayMessageTcs = null;

    public void ResetNewWindow() => _newWindowTcs = null;

    public async Task<string> WaitOverlayMessageAsync(int timeoutMs = 10000)
    {
        _overlayMessageTcs ??= new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return await _overlayMessageTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public async Task<(string Uri, bool IsUserInitiated)> WaitNewWindowAsync(int timeoutMs = 10000)
    {
        _newWindowTcs ??= new TaskCompletionSource<(string, bool)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return await _newWindowTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public async Task<bool> WaitOverlayTitleAsync(string expected, int timeoutMs = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var title = ProbeUtil.Unwrap(await RunOverlayAsync("document.title"));
                if (title.Contains(expected, StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // overlay mid-navigation
            }
            await Task.Delay(200);
        }
        return false;
    }
}
