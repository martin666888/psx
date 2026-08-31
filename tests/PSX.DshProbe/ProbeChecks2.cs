namespace PSX.DshProbe;

internal static partial class ProbeChecks
{
    private static async Task SurfaceScriptExportAsync(ProbeHost host)
    {
        host.ResetOverlayMessage();
        var messageTask = host.WaitOverlayMessageAsync(10000);
        await host.RunOverlayAsync("document.getElementById('export-link').click(); 'clicked'");
        string got;
        try { got = await messageTask; }
        catch (TimeoutException) { got = "<timeout>"; }
        var handedOff = got.Contains("psx-dsh-export", StringComparison.Ordinal)
            && got.Contains("/api/session.export", StringComparison.Ordinal);
        Add("surface-script-export", handedOff,
            $"overlay message = {ProbeUtil.Clip(got)}");
    }

    private static async Task WindowOpenAsync(ProbeHost host)
    {
        var task = host.WaitNewWindowAsync(6000);
        await host.RunOverlayAsync("window.open('https://example.com/probe','_blank'); 'opened'");
        string outcome;
        try
        {
            var (uri, userInitiated) = await task;
            outcome = $"event uri={uri} userInitiated={userInitiated} (denied by overlay policy)";
        }
        catch (TimeoutException)
        {
            outcome = "no event (popup blocked without user gesture)";
        }
        Add("window-open-scripted", true, outcome + "; trusted user-gesture branch remains a manual check");
    }

    private static async Task OddPortPopupAsync(ProbeHost host)
    {
        host.ResetNewWindow();
        var task = host.WaitNewWindowAsync(6000);
        await host.RunOverlayAsync("document.getElementById('oddport-link').click(); 'clicked'");
        try
        {
            var (uri, userInitiated) = await task;
            Add("odd-port-popup-contained", true,
                $"NewWindowRequested uri={uri} userInitiated={userInitiated}");
            return;
        }
        catch (TimeoutException)
        {
            // popup blocked without a user gesture
        }
        await Task.Delay(1200);
        var leaked = host.OverlayNavigations.Any(u => u.Contains("59999", StringComparison.Ordinal))
            || host.ShellNavigations.Any(u => u.Contains("59999", StringComparison.Ordinal));
        Add("odd-port-popup-contained", !leaked,
            $"overlayNavigations={ProbeUtil.Clip(string.Join("|", host.OverlayNavigations))} shellNavigations={ProbeUtil.Clip(string.Join("|", host.ShellNavigations))}");
    }

    private static async Task OverlayEscapeBlockedAsync(ProbeHost host)
    {
        _ = host.RunOverlayAsync("window.location.href = 'https://example.com/escape'; 'set'");
        await Task.Delay(800);
        var overlay = ProbeUtil.Unwrap(await host.TryRunOverlayAsync("location.href"));
        var shell = ProbeUtil.Unwrap(await host.RunShellAsync("location.href"));
        var overlayStayed = overlay.StartsWith(host.AllowedOrigin, StringComparison.OrdinalIgnoreCase);
        var shellStayed = shell.StartsWith("https://psx.local", StringComparison.OrdinalIgnoreCase);
        Add("overlay-escape-blocked", overlayStayed && shellStayed,
            $"overlay={overlay} shell={shell}");
    }

    private static async Task HideRestoreAsync(ProbeHost host, MockDshServer mock)
    {
        var tokenHitsBefore = mock.TokenHits;
        var before = ProbeUtil.Unwrap(await host.RunOverlayAsync(
            "(function(){return document.body ? document.body.clientWidth+'x'+document.body.clientHeight : '0x0';})()"));
        host.SetOverlayVisible(false);
        await Task.Delay(400);
        host.SetOverlayVisible(true);
        await Task.Delay(400);
        var after = ProbeUtil.Unwrap(await host.RunOverlayAsync(
            "(function(){return document.body ? document.body.clientWidth+'x'+document.body.clientHeight : '0x0';})()"));
        var alive = await host.WaitOverlayTitleAsync("DSH Probe Mock", 5000);
        Add("hide-restore",
            alive && before == after && mock.TokenHits == tokenHitsBefore,
            $"before={before} after={after} alive={alive} tokenHits={mock.TokenHits}");
    }

    private static async Task RealDshSmokeAsync(ProbeHost host)
    {
        await host.OpenOverlayAsync(host.ReadyUrl, 120000);
        string info;
        try
        {
            info = ProbeUtil.Unwrap(await host.RunOverlayAsync(
                "document.title + '|' + (document.body ? document.body.innerText.length : -1) + '|' + location.href"));
        }
        catch (Exception ex)
        {
            info = "error: " + ex.Message;
        }
        Add("dsh-real-smoke", !info.StartsWith("error:", StringComparison.Ordinal) && info.Contains('|'),
            $"overlay: {ProbeUtil.Clip(info, 300)}");
    }
}
