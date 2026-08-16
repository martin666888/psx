using System.IO;

namespace PSX.Services;

/// <summary>
/// Append-only diagnostic log with a hard size cap: once the file exceeds the
/// cap it is discarded (not rotated) so an unattended supervisor can never
/// exhaust the disk. Best-effort — logging must never throw.
/// </summary>
internal static class RotatingDiagnosticLog
{
    private const long MaximumBytes = 1_048_576;
    private static readonly object Sync = new();

    public static void AppendLine(string path, string message)
    {
        try
        {
            lock (Sync)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaximumBytes)
                    File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
