using System.Diagnostics;
using System.Text.Json;

namespace PSX.DshProbe;

/// <summary>A probe observation. <paramref name="KnownFinding"/> marks a
/// failure that reproduces a documented WebView2 platform behavior (see
/// tools/dsh-probe/P0-FINDINGS.md): it stays in the report as evidence but
/// does not fail the gate — only unknown regressions exit non-zero.</summary>
internal sealed record ProbeCheck(string Name, bool Pass, string Note, bool KnownFinding = false);

internal static class ProbeUtil
{
    /// <summary>ExecuteScriptAsync returns JSON-encoded values; unwrap the
    /// top-level string (and tolerate raw results).</summary>
    public static string Unwrap(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null")
            return "";
        try { return JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch { return raw; }
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs, int intervalMs = 100)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(intervalMs);
        }
        return condition();
    }

    public static string Clip(string value, int max = 220) =>
        value.Length <= max ? value : value[..max] + "...";
}
