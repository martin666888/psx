// PSX.DshProbe — Full-gate WebView2 probe for the DeepSeek Harness overlay.
// NOT product code. Mirrors the production path: a shell WebView (psx.local
// hole, no iframe) plus a second top-level WebView2 that opens the one-time
// ?token= Ready URL so the Strict session cookie is first-party. Optional
// --dsh-origin smoke-checks a real `dsh web` instance. Artifacts land under
// TestResults/dsh-probe/.
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
        var overlayUrl = realDsh ? new Uri(dshOrigin!) : mock.TokenUrl;
        Console.WriteLine($"[probe] mode={(realDsh ? "real-dsh" : "mock")} overlay={ProbeUtil.OriginOf(overlayUrl)} report={reportDir}");

        var shellDir = Path.Combine(reportDir, "shell");
        Directory.CreateDirectory(shellDir);
        WriteShellPage(Path.Combine(shellDir, "index.html"));

        var host = new ProbeHost(reportDir, shellDir, overlayUrl);
        using var watchdog = new System.Threading.Timer(
            _ => Environment.Exit(2), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        var code = await RunHostAsync(host, mock, realDsh, overlayUrl, reportDir).ConfigureAwait(false);
        Console.WriteLine("[probe] hard exit " + code);
        Environment.Exit(code);
        return code;
    }

    private static async Task<int> RunHostAsync(
        ProbeHost host, MockDshServer mock, bool realDsh, Uri overlayUrl, string reportDir)
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
            "trusted-click inside the overlay WebView -> column focus / tab highlight",
            "real IME composition (CJK) inside the overlay",
            "image drag & drop / attachments / clipboard paste",
            "native directory picker (webkitdirectory / File System Access)",
            "real DSH: long conversation, WebSocket stability, session ZIP export click"
        };
        var report = new
        {
            probe = "dsh-webview-overlay",
            mode = realDsh ? "real-dsh" : "mock",
            mockOrigin = mock.BaseUri.AbsoluteUri,
            overlayOrigin = ProbeUtil.OriginOf(overlayUrl),
            checks = ProbeChecks.Results.Select(c => new { c.Name, c.Pass, c.Note }),
            manual
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(reportDir, realDsh ? "webview-real-report.json" : "webview-report.json");
        await File.WriteAllTextAsync(reportPath, json, new UTF8Encoding(false)).ConfigureAwait(false);
        Console.WriteLine("[probe] report written: " + reportPath);

        var failed = ProbeChecks.Results.Count(c => !c.Pass);
        Console.WriteLine(
            $"[probe] {ProbeChecks.Results.Count - failed}/{ProbeChecks.Results.Count} checks passed, " +
            $"{failed} failed");
        Console.WriteLine("[probe] returning exit code " + (failed == 0 ? 0 : 1));
        return failed == 0 ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void WriteShellPage(string path)
    {
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
            "frame-src 'none'",
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
              <div id="dsh-hole" style="width:800px;height:600px" data-role="dsh-surface"></div>
            </body>
            </html>
            """;
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }
}
