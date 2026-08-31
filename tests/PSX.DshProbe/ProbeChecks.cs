using System.Net.Http;

namespace PSX.DshProbe;

/// <summary>
/// Full-gate checks for the production DSH overlay: second top-level
/// WebView2, one-time token exchange, Strict cookie, host-mediated export.
/// </summary>
internal static partial class ProbeChecks
{
    public static readonly List<ProbeCheck> Results = new();

    private static void Add(string name, bool pass, string note, bool knownFinding = false) =>
        Results.Add(new ProbeCheck(name, pass, note, knownFinding));

    public static async Task RunAllAsync(ProbeHost host, MockDshServer mock, bool realDsh)
    {
        await host.NavigateShellAsync("https://psx.local/index.html");
        if (realDsh)
        {
            await StepAsync("dsh-real-smoke", () => RealDshSmokeAsync(host));
            return;
        }

        await StepAsync("shell-has-no-iframe", () => ShellHasNoIframeAsync(host));
        await StepAsync("overlay-token-first-party", () => OverlayTokenFirstPartyAsync(host, mock));
        await StepAsync("health-root-unauthenticated", () => HealthRootUnauthenticatedAsync(host, mock));
        await StepAsync("surface-script-focus", () => SurfaceScriptFocusAsync(host));
        await StepAsync("surface-script-export", () => SurfaceScriptExportAsync(host));
        await StepAsync("unicode-input", () => UnicodeInputAsync(host));
        await StepAsync("websocket-echo", () => WebSocketEchoAsync(host));
        await StepAsync("window-open-scripted", () => WindowOpenAsync(host));
        await StepAsync("odd-port-popup-contained", () => OddPortPopupAsync(host));
        await StepAsync("overlay-escape-blocked", () => OverlayEscapeBlockedAsync(host));
        await StepAsync("hide-restore", () => HideRestoreAsync(host, mock));
    }

    private static async Task StepAsync(string name, Func<Task> step)
    {
        Console.WriteLine($"[probe] check: {name}");
        await step();
    }

    private static async Task ShellHasNoIframeAsync(ProbeHost host)
    {
        var count = ProbeUtil.Unwrap(await host.RunShellAsync(
            "String(document.querySelectorAll('iframe').length)"));
        var href = ProbeUtil.Unwrap(await host.RunShellAsync("location.href"));
        Add("shell-has-no-iframe",
            count == "0" && href.StartsWith("https://psx.local/", StringComparison.OrdinalIgnoreCase),
            $"iframes={count} href={href}");
    }

    private static async Task OverlayTokenFirstPartyAsync(ProbeHost host, MockDshServer mock)
    {
        var tokenHitsBefore = mock.TokenHits;
        await host.OpenOverlayAsync(mock.TokenUrl);
        var title = ProbeUtil.Unwrap(await host.TryRunOverlayAsync("document.title"));
        var href = ProbeUtil.Unwrap(await host.TryRunOverlayAsync("location.href"));
        var openedToken = host.OverlayNavigations.Any(uri =>
            uri.Contains("?token=", StringComparison.Ordinal));
        var cookieDocument = title == "DSH Probe Mock"
            && href.StartsWith(host.AllowedOrigin, StringComparison.OrdinalIgnoreCase)
            && !href.Contains("?token=", StringComparison.Ordinal);
        Add("overlay-token-first-party",
            openedToken && cookieDocument && mock.TokenHits == tokenHitsBefore + 1,
            $"title={title} href={href} tokenHits={mock.TokenHits} navigations={ProbeUtil.Clip(string.Join("|", host.OverlayNavigations))}");
    }

    private static async Task HealthRootUnauthenticatedAsync(ProbeHost host, MockDshServer mock)
    {
        var tokenHitsBefore = mock.TokenHits;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(host.AllowedOrigin + "/");
        Add("health-root-unauthenticated",
            (int)response.StatusCode == 401 && mock.TokenHits == tokenHitsBefore,
            $"status={(int)response.StatusCode} tokenHits={mock.TokenHits}");
    }

    private static async Task SurfaceScriptFocusAsync(ProbeHost host)
    {
        host.ResetOverlayMessage();
        var messageTask = host.WaitOverlayMessageAsync(10000);
        await host.RunOverlayAsync(
            "document.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })); 'sent'");
        string got;
        try { got = await messageTask; }
        catch (TimeoutException) { got = "<timeout>"; }
        Add("surface-script-focus",
            got.Contains("psx-dsh-focus", StringComparison.Ordinal),
            $"overlay message = {ProbeUtil.Clip(got)}");
    }

    private static async Task UnicodeInputAsync(ProbeHost host)
    {
        const string sample = "\u4e2d\u6587\u8f93\u5165\u6d4b\u8bd5\ud83d\ude42";
        var echoed = ProbeUtil.Unwrap(await host.RunOverlayAsync(
            "(function(){var i=document.getElementById('probe-input');i.value='" + sample + "';return i.value;})()"));
        Add("unicode-input", echoed == sample, $"echoed = {echoed}");
    }

    private static async Task WebSocketEchoAsync(ProbeHost host)
    {
        const string success = "ok:echo:ping";
        var first = await TryWebSocketOnceAsync(host);
        if (first == success)
        {
            Add("websocket-echo", true, $"attempt1={first}");
            return;
        }

        var second = await TryWebSocketOnceAsync(host);
        var recoveredFromIdle = first == "idle" && second == success;
        var knownIdle = first == "idle" && second == "idle";
        Add(
            "websocket-echo",
            recoveredFromIdle,
            $"attempt1={first}; attempt2={second}",
            knownFinding: knownIdle);
    }

    private static async Task<string> TryWebSocketOnceAsync(ProbeHost host)
    {
        await host.RunOverlayAsync(
            "window.__probeWsResult='idle'; probeWs().then(function(v){window.__probeWsResult='ok:'+v;},function(e){window.__probeWsResult='err:'+(e && e.message ? e.message : e);}); 'started'");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var state = "idle";
        while (watch.ElapsedMilliseconds < 12000 && state == "idle")
        {
            state = ProbeUtil.Unwrap(await host.TryRunOverlayAsync("window.__probeWsResult"));
            if (state != "idle") break;
            await Task.Delay(200);
        }
        return state;
    }
}
