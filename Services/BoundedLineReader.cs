using System.IO;
using System.Text;

namespace PSX.Services;

/// <summary>
/// Reads a byte stream as UTF-8 line-delimited text under a hard per-line cap.
/// Accumulation never exceeds the cap: a line longer than
/// <paramref name="maxLineChars"/> aborts the read with false instead of being
/// materialized; a line exactly at the cap is delivered normally. Both LF and
/// CRLF terminate a line, and a non-empty unterminated tail is delivered as a
/// final line. The callback may also abort the read by returning false.
/// </summary>
internal static class BoundedLineReader
{
    private const int ReadBufferBytes = 64 * 1024;

    public static async Task<bool> ReadLinesAsync(
        Stream stream,
        int maxLineChars,
        Func<string, bool> onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);

        var bytes = new byte[ReadBufferBytes];
        var chars = new char[ReadBufferBytes];
        var decoder = Encoding.UTF8.GetDecoder();
        var line = new StringBuilder(Math.Min(maxLineChars, ReadBufferBytes));

        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            var offset = 0;
            while (offset < count)
            {
                var remaining = chars.AsSpan(offset, count - offset);
                var newlineIndex = remaining.IndexOf('\n');
                var pieceLength = newlineIndex >= 0 ? newlineIndex : remaining.Length;
                if (line.Length + pieceLength > maxLineChars)
                    return false;

                line.Append(chars, offset, pieceLength);
                offset += pieceLength;
                if (newlineIndex < 0)
                    continue;

                offset++;
                if (!onLine(TakeLine(line)))
                    return false;
            }
        }

        var flushed = decoder.GetChars(bytes, 0, 0, chars, 0, flush: true);
        if (flushed > 0)
        {
            if (line.Length + flushed > maxLineChars)
                return false;
            line.Append(chars, 0, flushed);
        }

        return line.Length == 0 || onLine(TakeLine(line));
    }

    private static string TakeLine(StringBuilder line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
            length--;
        var text = line.ToString(0, length);
        line.Clear();
        return text;
    }
}
