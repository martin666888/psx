// PSX.DshProbe — P0 WebView2 embedding probe for the DeepSeek Harness (DSH)
// third-workspace experiment. NOT product code; part of PSX.slnx and run by
// the Full gate in mock mode: it answers the WebView2 boundary questions
// (mixed content, CSP frame-src, frame injection, focus, downloads, popups,
// WebSocket, hide/restore) against a mock DSH server, and optionally
// smoke-checks the real `dsh web` app in the same iframe (--dsh-origin).
// All artifacts land under TestResults/dsh-probe/.
using System.Text;
using System.Text.Json;

namespace PSX.DshProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();

    private static async Task<int> MainAsync(string[] args)
    {
        var dshOrigin = ArgValue(args, "--dsh-origin");
        var realDsh = dshOrigin is not null;
        var reportDir = Path.GetFullPath(ArgValue(args, "--report-dir") ??
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "dsh-probe"));
        Directory.CreateDirectory(reportDir);

        using var mock = MockDshServer.Start();
        var targetOrigin = realDsh ? new Uri(dshOrigin!) : mock.BaseUri;
        Console.WriteLine($"[probe] mode={(realDsh ? "real-dsh" : "mock")} target={targetOrigin} report={reportDir}");

        var shellDir = Path.Combine(reportDir, "shell");
        Directory.CreateDirectory(shellDir);
        WriteShellPage(Path.Combine(shellDir, "index.html"), targetOrigin.AbsoluteUri, relaxed: true);
        WriteShellPage(Path.Combine(shellDir, "index-legacy.html"), targetOrigin.AbsoluteUri, relaxed: false);
        WriteShellPage(Path.Combine(shellDir, "index-nosandbox.html"), targetOrigin.AbsoluteUri, relaxed: true, sandboxed: false);

        var host = new ProbeHost(reportDir, shellDir, targetOrigin);
        // Hard watchdog on the thread pool: Environment.Exit needs no UI
        // thread and no continuation, so it fires even when the WebView2 UI
        // thread wedges.
        using var watchdog = new System.Threading.Timer(
            _ => Environment.Exit(2), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        var code = await RunHostAsync(host, mock, realDsh, targetOrigin, reportDir).ConfigureAwait(false);
        // Hard exit: graceful teardown deadlocks once the UI pump is gone.
        Console.WriteLine("[probe] hard exit " + code);
        Environment.Exit(code);
        return code;
    }

    private static async Task<int> RunHostAsync(
        ProbeHost host, MockDshServer mock, bool realDsh, Uri targetOrigin, string reportDir)
    {
        try
        {
            await host.RunAsync(async h =>
            {
                try
                {
                    await ProbeChecks.RunAllAsync(h, mock, realDsh);
                }
                catch (Exception ex)
                {
                    ProbeChecks.Results.Add(new ProbeCheck("host-run", false, ex.ToString()));
                }
            });
        }
        finally
        {
            host.Dispose();
        }

        var manual = new[]
        {
            "trusted-click inside iframe -> column focus / tab highlight (real user gesture; optional CDP layer via WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port)",
            "real IME composition (CJK) inside the iframe",
            "image drag & drop / attachments / clipboard paste",
            "native directory picker (webkitdirectory / File System Access)",
            "real DSH: long conversation, WebSocket stability, session ZIP export click"
        };
        var report = new
        {
            probe = "dsh-webview-embed",
            mode = realDsh ? "real-dsh" : "mock",
            mockOrigin = mock.BaseUri.AbsoluteUri,
            targetOrigin = targetOrigin.AbsoluteUri,
            checks = ProbeChecks.Results.Select(c => new { c.Name, c.Pass, c.Note }),
            manual
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(reportDir, realDsh ? "webview-real-report.json" : "webview-report.json");
        await File.WriteAllTextAsync(reportPath, json, new UTF8Encoding(false)).ConfigureAwait(false);
        Console.WriteLine("[probe] report written: " + reportPath);

        var failed = ProbeChecks.Results.Count(c => !c.Pass);
        Console.WriteLine($"[probe] {ProbeChecks.Results.Count - failed}/{ProbeChecks.Results.Count} checks passed, " + failed + " failed");
        Console.WriteLine("[probe] returning exit code " + (failed == 0 ? 0 : 1));
        return failed == 0 ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void WriteShellPage(string path, string origin, bool relaxed, bool sandboxed = true)
    {
        var frameSrc = relaxed ? "http://127.0.0.1:*" : "'none'";
        var sandboxAttr = sandboxed
            ? "sandbox=\"allow-scripts allow-same-origin allow-forms allow-downloads allow-popups allow-modals\""
            : "";
        var csp = string.Join("; ", new[]
        {
            "default-src 'none'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob: https://psx-attachments.local",
            "font-src 'self' data:",
            "connect-src 'self'",
            "media-src 'self' blob: https://psx-attachments.local",
            "worker-src 'self' blob:",
            "object-src 'none'",
            $"frame-src {frameSrc}",
            "base-uri 'none'",
            "form-action 'none'"
        });
        var html = $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta http-equiv="Content-Security-Policy" content="{{csp}}">
              <title>PSX DSH Probe Shell</title>
            </head>
            <body>
              <a id="top-export-link" href="{{origin}}api/session.export">top export</a>
              <iframe id="dsh" name="dsh"
                      {{sandboxAttr}}
                      src="{{origin}}" style="width:800px;height:600px;border:0"></iframe>
            </body>
            </html>
            """;
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }
}
