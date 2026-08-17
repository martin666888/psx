namespace PSX.DshProbe;

/// <summary>
/// The P0 veto checks. Each check records pass/fail with a note; the host
/// writes the whole report to TestResults/dsh-probe/. Manual-only items
/// (trusted-gesture clicks, real IME, drag &amp; drop, native pickers) are
/// listed in Program's report and stay out of the automated pass gate.
/// </summary>
internal static partial class ProbeChecks
{
    public static readonly List<ProbeCheck> Results = new();

    private static void Add(string name, bool pass, string note, bool knownFinding = false) =>
        Results.Add(new ProbeCheck(name, pass, note, knownFinding));

    public static async Task RunAllAsync(ProbeHost host, MockDshServer mock, bool realDsh)
    {
        if (realDsh)
        {
            await StepAsync("dsh-real-smoke", () => RealDshSmokeAsync(host));
            return;
        }
        await StepAsync("legacy-csp-blocks-frame", () => LegacyCspBlocksAsync(host));
        await StepAsync("mixed-content-frame-loads", () => MixedContentLoadsAsync(host, mock));
        await StepAsync("frame-injection-pointerdown", () => InjectionAndPointerdownAsync(host));
        await StepAsync("iframe-focus-activeElement", () => FrameFocusAsync(host));
        await StepAsync("unicode-input", () => UnicodeInputAsync(host));
        await StepAsync("websocket-echo", () => WebSocketEchoAsync(host));
        await StepAsync("export-download-blob", () => ExportDownloadBlobAsync(host));
        await StepAsync("export-download-zip", () => ExportDownloadAsync(host));
        await StepAsync("export-download-top", () => ExportDownloadTopAsync(host));
        await StepAsync("window-open-scripted", () => WindowOpenAsync(host));
        await StepAsync("odd-port-popup-contained", () => OddPortPopupAsync(host));
        await StepAsync("top-navigation-blocked", () => TopEscapeBlockedAsync(host));
        await StepAsync("hide-restore", () => HideRestoreAsync(host));
        await StepAsync("export-download-nosandbox", () => ExportDownloadNoSandboxAsync(host));
    }

    private static async Task StepAsync(string name, Func<Task> step)
    {
        Console.WriteLine($"[probe] check: {name}");
        await step();
    }

    /// <summary>Control experiment: under frame-src 'none' the frame must not
    /// actually load the DSH document. NOTE: FrameNavigationStarting still
    /// fires (the host sees the request before the renderer enforces CSP), so
    /// the decisive signal is that the mock page's document never becomes
    /// readable inside the frame.</summary>
    private static async Task LegacyCspBlocksAsync(ProbeHost host)
    {
        host.ResetFrameWaiters();
        await host.NavigateAsync("https://psx.local/index-legacy.html");
        await Task.Delay(3000);
        var kind = ProbeUtil.Unwrap(await host.RunTopAsync(
            "(function(){try{var d=document.getElementById('dsh').contentDocument;return d===null?'cross-origin':'same-origin';}catch(e){return 'cross-origin';}})()"));
        var title = ProbeUtil.Unwrap(await host.TryRunFrameAsync("document.title"));
        var navigations = host.FrameNavigations.Count;
        var pass = title != "DSH Probe Mock";
        Add("legacy-csp-blocks-frame", pass,
            $"frame-src 'none': frameNavigations={navigations} contentDocument={kind} frameTitle='{title}' (navigation event fires before renderer CSP enforcement)");
    }

    /// <summary>The core veto: https://psx.local embedding http://127.0.0.1:*
    /// must not be treated as mixed content, and the frame must load.</summary>
    private static async Task MixedContentLoadsAsync(ProbeHost host, MockDshServer mock)
    {
        host.ResetFrameWaiters();
        await host.NavigateAsync("https://psx.local/index.html");
        await host.EnsureFrameAsync(20000);
        var kind = ProbeUtil.Unwrap(await host.RunTopAsync(
            "(function(){try{var d=document.getElementById('dsh').contentDocument;return d===null?'cross-origin':'same-origin';}catch(e){return 'cross-origin';}})()"));
        var title = ProbeUtil.Unwrap(await host.TryRunFrameAsync("document.title"));
        var uriOk = host.DshFrameUri != null
            && host.DshFrameUri.StartsWith(host.AllowedOrigin, StringComparison.OrdinalIgnoreCase);
        Add("mixed-content-frame-loads",
            kind == "cross-origin" && title == "DSH Probe Mock" && uriOk,
            $"contentDocument={kind} title={title} frameUri={host.DshFrameUri} expected={mock.BaseUri.AbsoluteUri}");
    }

    /// <summary>Host-injected frame script must survive navigation and deliver
    /// pointerdown via the frame's own WebMessageReceived channel (the Phase 3
    /// focus-injection surface).</summary>
    private static async Task InjectionAndPointerdownAsync(ProbeHost host)
    {
        host.ResetFrameWaiters();
        await host.InjectFrameScriptAsync(
            "(function(){ if (location.origin !== '" + host.AllowedOrigin + "') return;"
            + " window.addEventListener('pointerdown', function () { try { window.chrome.webview.postMessage(JSON.stringify({ probe: 'pointerdown' })); } catch (e) {} }); })();");
        await host.NavigateAsync("https://psx.local/index.html");
        await host.EnsureFrameAsync(20000);
        await CheckPointerdownAsync(host);
        // The injection must also survive document recreation. NOTE: driving
        // the reload via frame ExecuteScriptAsync("location.reload()") would
        // hang forever — the script's result never resolves once it destroys
        // its own document (a known WebView2 trap). Re-navigate the top
        // document instead: the injected script persists at the CoreWebView2
        // level and runs again in the fresh frame.
        host.ResetFrameWaiters();
        await host.NavigateAsync("https://psx.local/index.html");
        await host.EnsureFrameAsync(20000);
        await CheckPointerdownAsync(host);
    }

    private static async Task CheckPointerdownAsync(ProbeHost host)
    {
        host.ResetFrameMessage();
        var messageTask = host.WaitFrameMessageAsync(10000);
        await host.RunFrameAsync("document.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })); 'sent'");
        string got;
        try { got = await messageTask; }
        catch (TimeoutException) { got = "<timeout>"; }
        Add("frame-injection-pointerdown", got.Contains("pointerdown", StringComparison.Ordinal),
            $"frame message = {ProbeUtil.Clip(got)}");
    }

    /// <summary>When the frame's input takes DOM focus, the parent document's
    /// activeElement must become the iframe element — the click-to-focus-column
    /// mechanism the DshWorkspaceHost will rely on.</summary>
    private static async Task FrameFocusAsync(ProbeHost host)
    {
        host.FocusWindow();
        await Task.Delay(400);
        await host.RunFrameAsync("document.getElementById('probe-input').focus(); 'focused'");
        await Task.Delay(400);
        var active = ProbeUtil.Unwrap(await host.RunTopAsync(
            "document.activeElement === document.getElementById('dsh') ? 'iframe' : (document.activeElement ? document.activeElement.tagName : 'none')"));
        Add("iframe-focus-activeElement", active == "iframe", $"parent activeElement = {active}");
    }

    private static async Task UnicodeInputAsync(ProbeHost host)
    {
        const string sample = "\u4e2d\u6587\u8f93\u5165\u6d4b\u8bd5\ud83d\ude42";
        var echoed = ProbeUtil.Unwrap(await host.RunFrameAsync(
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
        // A first idle followed by a successful retry is the documented
        // background-throttling flake and proves the WebSocket path works.
        // The report-only known finding is narrower still: both attempts must
        // remain exactly idle. Never overwrite a concrete error or malformed
        // echo with a later idle/success result.
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
        await host.RunFrameAsync(
            "window.__probeWsResult='idle'; probeWs().then(function(v){window.__probeWsResult='ok:'+v;},function(e){window.__probeWsResult='err:'+(e && e.message ? e.message : e);}); 'started'");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var state = "idle";
        while (watch.ElapsedMilliseconds < 12000 && state == "idle")
        {
            state = ProbeUtil.Unwrap(await host.TryRunFrameAsync("window.__probeWsResult"));
            if (state != "idle") break;
            await Task.Delay(200);
        }
        return state;
    }
}
