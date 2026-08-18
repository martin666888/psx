using System.IO.Compression;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace PSX.DshProbe;

internal static partial class ProbeChecks
{
    /// <summary>DownloadStarting must fire for the iframe's export link, allow
    /// a host ResultFilePath (sandbox allow-downloads is required), and land a
    /// real ZIP with the expected entry.</summary>
    private static async Task ExportDownloadAsync(ProbeHost host)
    {
        // Diagnose the server round-trip first, so a transport failure cannot
        // be misread as a download-pipeline failure.
        await host.RunFrameAsync(
            "fetch('/api/session.export').then(function(r){window.__exportProbe='status:'+r.status;},function(e){window.__exportProbe='err:'+e;}); 'fetching'");
        await Task.Delay(1500);
        var serverNote = ProbeUtil.Unwrap(await host.TryRunFrameAsync("window.__exportProbe"));

        // Trigger path 1: anchor click.
        host.ResetDownload();
        var downloadTask = host.WaitDownloadAsync(12000);
        await host.RunFrameAsync("document.getElementById('export-link').click(); 'clicked'");
        try
        {
            var operation = await downloadTask;
            await ValidateDownloadAsync(host, operation, "export-download-zip");
            return;
        }
        catch (TimeoutException)
        {
            // trigger path 2 below
        }

        // Trigger path 2: navigate the frame to the attachment URL. The frame
        // document is destroyed, so the script result never resolves — fire
        // and forget.
        host.ResetDownload();
        var downloadTask2 = host.WaitDownloadAsync(12000);
        _ = host.RunFrameAsync("location.href='/api/session.export'");
        try
        {
            var operation = await downloadTask2;
            await ValidateDownloadAsync(host, operation, "export-download-zip");
            return;
        }
        catch (TimeoutException)
        {
            // Known finding: DownloadStarting never fires in this WebView2
            // environment (P0-FINDINGS.md); the product exports through the
            // host-mediated channel instead.
            Add("export-download-zip", false,
                $"DownloadStarting never fired (click or navigation); frame fetch server probe = {serverNote}",
                knownFinding: true);
        }
    }

    /// <summary>Decisive control: a blob download from the TOP document — no
    /// HTTP, no server, no cross-scheme. Settles whether WebView2 surfaces
    /// downloads at all, independent of the mock server and mixed content.</summary>
    private static async Task ExportDownloadBlobAsync(ProbeHost host)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("session.txt");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("probe");
        }
        var b64 = Convert.ToBase64String(ms.ToArray());
        host.ResetDownload();
        var downloadTask = host.WaitDownloadAsync(12000);
        await host.RunTopAsync(
            "(function(){var b='" + b64 + "';var bin=atob(b);var arr=new Uint8Array(bin.length);for(var i=0;i<bin.length;i++)arr[i]=bin.charCodeAt(i);"
            + "var blob=new Blob([arr],{type:'application/zip'});var url=URL.createObjectURL(blob);"
            + "var a=document.createElement('a');a.href=url;a.download='session.zip';document.body.appendChild(a);a.click();return 'clicked';})()");
        try
        {
            var operation = await downloadTask;
            var bytesOk = false;
            var note = "";
            var file = Path.Combine(host.ReportDir, "export.zip");
            try
            {
                if (File.Exists(file))
                {
                    var bytes = await File.ReadAllBytesAsync(file);
                    if (bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
                    {
                        using var zip = ZipFile.OpenRead(file);
                        bytesOk = zip.Entries.Any(e => e.Name == "session.txt");
                        note = $"entries={zip.Entries.Count}";
                    }
                    else note = $"not a zip (len={bytes.Length})";
                }
                else note = "file missing";
            }
            catch (Exception ex) { note = ex.Message; }
            Add("export-download-blob", bytesOk, $"uri={operation.Uri} state={operation.State} {note}");
        }
        catch (TimeoutException)
        {
            // Known finding (P0-FINDINGS.md): no download pipeline at all.
            Add("export-download-blob", false, "DownloadStarting never fired for a top-level blob download",
                knownFinding: true);
        }
    }

    /// <summary>Control variant: a top-level anchor download from the shell
    /// page to the same mock URL — isolates whether DownloadStarting is
    /// suppressed by the cross-origin frame, by the sandbox, by the missing
    /// user gesture, or by the SDK generally.</summary>
    private static async Task ExportDownloadTopAsync(ProbeHost host)
    {
        // Trigger 1: scripted click (no user activation).
        host.ResetDownload();
        var downloadTask = host.WaitDownloadAsync(12000);
        await host.RunTopAsync("document.getElementById('top-export-link').click(); 'clicked'");
        try
        {
            var operation = await downloadTask;
            await ValidateDownloadAsync(host, operation, "export-download-top");
            return;
        }
        catch (TimeoutException)
        {
            // trigger 2 below
        }

        // Trigger 2: a REAL user gesture via DevTools Protocol input dispatch
        // (trusted events with user activation).
        host.ResetDownload();
        var downloadTask2 = host.WaitDownloadAsync(12000);
        var downloadsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "session.zip");
        try { File.Delete(downloadsFile); } catch { /* best effort */ }
        string cdpResult = "not-called";
        try
        {
            var rectJson = ProbeUtil.Unwrap(await host.RunTopAsync(
                "(function(){var r=document.getElementById('top-export-link').getBoundingClientRect();return JSON.stringify({x:Math.round(r.left+r.width/2),y:Math.round(r.top+r.height/2)});})()"));
            using var rectDoc = System.Text.Json.JsonDocument.Parse(rectJson);
            var x = rectDoc.RootElement.GetProperty("x").GetInt32();
            var y = rectDoc.RootElement.GetProperty("y").GetInt32();
            cdpResult = await host.SendCdpAsync("Input.dispatchMouseEvent",
                $"{{\"type\":\"mousePressed\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
            cdpResult += " | " + await host.SendCdpAsync("Input.dispatchMouseEvent",
                $"{{\"type\":\"mouseReleased\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        }
        catch (Exception ex)
        {
            cdpResult = "exception: " + ex.Message;
        }
        try
        {
            var operation2 = await downloadTask2;
            await ValidateDownloadAsync(host, operation2, "export-download-top");
            return;
        }
        catch (TimeoutException)
        {
            // record below
        }
        var topHref = ProbeUtil.Unwrap(await host.RunTopAsync("location.href"));
        // Known finding (P0-FINDINGS.md): even a trusted CDP click never
        // surfaces DownloadStarting in this environment.
        Add("export-download-top", false,
            "no DownloadStarting even for a trusted CDP click; cdpResult=" + ProbeUtil.Clip(cdpResult)
            + " top=" + topHref
            + " navigations=" + ProbeUtil.Clip(string.Join("|", host.TopNavigations))
            + " downloadsSessionZip=" + File.Exists(downloadsFile),
            knownFinding: true);
    }

    /// <summary>Control variant: the same iframe download without the sandbox
    /// attribute — isolates sandbox-token behavior from cross-origin behavior.</summary>
    private static async Task ExportDownloadNoSandboxAsync(ProbeHost host)
    {
        host.ResetFrameWaiters();
        await host.NavigateAsync("https://psx.local/index-nosandbox.html");
        await host.EnsureFrameAsync(20000);
        var title = ProbeUtil.Unwrap(await host.TryRunFrameAsync("document.title"));
        if (title != "DSH Probe Mock")
        {
            Add("export-download-nosandbox", false, $"nosandbox frame did not load (title={title})");
            return;
        }
        host.ResetDownload();
        var downloadTask = host.WaitDownloadAsync(12000);
        await host.RunFrameAsync("document.getElementById('export-link').click(); 'clicked'");
        try
        {
            var operation = await downloadTask;
            await ValidateDownloadAsync(host, operation, "export-download-nosandbox");
        }
        catch (TimeoutException)
        {
            // Known finding (P0-FINDINGS.md): the sandbox is not the cause —
            // downloads never surface with or without it.
            Add("export-download-nosandbox", false, "DownloadStarting never fired even without the iframe sandbox",
                knownFinding: true);
        }
    }

    private static async Task ValidateDownloadAsync(
        ProbeHost host, CoreWebView2DownloadOperation operation, string checkName)
    {
        var pathOk = new Uri(operation.Uri).AbsolutePath == "/api/session.export";
        var bytesOk = false;
        var note = "";
        var file = Path.Combine(host.ReportDir, "export.zip");
        try
        {
            if (File.Exists(file))
            {
                var bytes = await File.ReadAllBytesAsync(file);
                if (bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
                {
                    using var zip = ZipFile.OpenRead(file);
                    bytesOk = zip.Entries.Any(e => e.Name == "session.txt");
                    note = $"entries={zip.Entries.Count}";
                }
                else
                {
                    note = $"not a zip (len={bytes.Length})";
                }
            }
            else
            {
                note = "file missing";
            }
        }
        catch (Exception ex)
        {
            note = ex.Message;
        }
        Add(checkName, pathOk && bytesOk,
            $"uri={operation.Uri} state={operation.State} path={new Uri(operation.Uri).AbsolutePath} {note}");
    }

    /// <summary>Script-initiated window.open has no user gesture: record what
    /// the WebView2 surface does (Chromium popup blocker may suppress it
    /// entirely). The trusted-gesture branch is a manual check.</summary>
    private static async Task WindowOpenAsync(ProbeHost host)
    {
        var task = host.WaitNewWindowAsync(6000);
        await host.RunFrameAsync("window.open('https://example.com/probe','_blank'); 'opened'");
        string outcome;
        try
        {
            var (uri, userInitiated) = await task;
            outcome = $"event uri={uri} userInitiated={userInitiated} (denied by probe policy)";
        }
        catch (TimeoutException)
        {
            outcome = "no event (popup blocked without user gesture)";
        }
        Add("window-open-scripted", true, outcome + "; trusted user-gesture branch remains a manual check");
    }

    /// <summary>A popup/navigation to a non-default-port loopback URL must not
    /// leak out of the frame surface.</summary>
    private static async Task OddPortPopupAsync(ProbeHost host)
    {
        host.ResetNewWindow();
        var task = host.WaitNewWindowAsync(6000);
        await host.RunFrameAsync("document.getElementById('oddport-link').click(); 'clicked'");
        try
        {
            var (uri, userInitiated) = await task;
            Add("odd-port-popup-contained", true,
                $"NewWindowRequested uri={uri} userInitiated={userInitiated} — Phase 3 Classify will mark it Blocked");
            return;
        }
        catch (TimeoutException)
        {
            // popup blocked by Chromium without a user gesture; still confirm
            // no navigation happened anywhere.
        }
        await Task.Delay(1200);
        var leaked = host.TopNavigations.Any(u => u.Contains("59999", StringComparison.Ordinal))
            || host.FrameNavigations.Any(u => u.Contains("59999", StringComparison.Ordinal));
        Add("odd-port-popup-contained", !leaked,
            $"topNavigations={ProbeUtil.Clip(string.Join("|", host.TopNavigations))} frameNavigations={ProbeUtil.Clip(string.Join("|", host.FrameNavigations))}");
    }

    /// <summary>The sandbox must stop the frame from navigating the top-level
    /// document away from psx.local.</summary>
    private static async Task TopEscapeBlockedAsync(ProbeHost host)
    {
        var error = "";
        try
        {
            await host.RunFrameAsync("window.top.location.href = 'https://example.com/escape'; 'set'");
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        await Task.Delay(800);
        var top = ProbeUtil.Unwrap(await host.RunTopAsync("location.href"));
        var escaped = !top.StartsWith("https://psx.local", StringComparison.OrdinalIgnoreCase);
        Add("top-navigation-blocked", !escaped,
            $"top={top} frameError={ProbeUtil.Clip(error, 120)}");
    }

    /// <summary>Hiding the iframe (background tab) and restoring it must keep
    /// the frame alive with its geometry intact — no reload.</summary>
    private static async Task HideRestoreAsync(ProbeHost host)
    {
        var before = ProbeUtil.Unwrap(await host.RunTopAsync(
            "(function(){var f=document.getElementById('dsh');return f.clientWidth+'x'+f.clientHeight;})()"));
        await host.RunTopAsync("document.getElementById('dsh').style.display='none'; 'hidden'");
        await Task.Delay(400);
        await host.RunTopAsync("document.getElementById('dsh').style.display=''; 'shown'");
        await Task.Delay(400);
        var after = ProbeUtil.Unwrap(await host.RunTopAsync(
            "(function(){var f=document.getElementById('dsh');return f.clientWidth+'x'+f.clientHeight;})()"));
        var alive = await host.WaitFrameTitleAsync("DSH Probe Mock", 5000);
        Add("hide-restore", alive && before == after, $"before={before} after={after} frameAlive={alive}");
    }

    /// <summary>Optional real-DSH smoke (--dsh-origin): the iframe loads the
    /// actual DSH SPA served by the launch probe's `dsh web` instance.</summary>
    private static async Task RealDshSmokeAsync(ProbeHost host)
    {
        host.ResetFrameWaiters();
        await host.NavigateAsync("https://psx.local/index.html");
        await host.EnsureFrameAsync(120000);
        string info;
        try
        {
            info = ProbeUtil.Unwrap(await host.RunFrameAsync(
                "document.title + '|' + (document.body ? document.body.innerText.length : -1) + '|' + location.href"));
        }
        catch (Exception ex)
        {
            info = "error: " + ex.Message;
        }
        Add("dsh-real-smoke", !info.StartsWith("error:", StringComparison.Ordinal) && info.Contains('|'),
            $"frame: {ProbeUtil.Clip(info, 300)}");
    }
}
