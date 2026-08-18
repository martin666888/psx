using System.IO;
using System.Text;

namespace PSX.Services;

/// <summary>
/// Append-only diagnostic log with a true size cap: a single entry is
/// truncated first, and the file is discarded (not rotated) as soon as the
/// existing bytes plus the incoming entry would exceed the cap, so an
/// unattended supervisor can never exhaust the disk. Best-effort — logging
/// must never throw.
/// </summary>
internal static class RotatingDiagnosticLog
{
    private const long MaximumBytes = 1_048_576;
    private const int MaximumMessageChars = 8 * 1024;
    // Timestamp "[O-format] " + newline + culture slack for the entry budget.
    private const int EntryOverheadBytes = 96;
    private static readonly object Sync = new();

    public static void AppendLine(string path, string message)
    {
        try
        {
            if (message.Length > MaximumMessageChars)
                message = message[..MaximumMessageChars] + "…[truncated]";
            var entryBytes = Encoding.UTF8.GetByteCount(message) + EntryOverheadBytes;

            lock (Sync)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length + entryBytes > MaximumBytes)
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
