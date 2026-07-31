using System.Buffers;
using System.IO;

namespace PSX.Services;

/// <summary>
/// Reads UTF-8 JSONL without ever materializing a whole file or an unbounded
/// line. The callback must consume the memory before returning.
/// </summary>
internal static class BoundedJsonlReader
{
    public const int MaxLineBytes = 16 * 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;

    public static int Read(
        string path,
        Action<ReadOnlyMemory<byte>> onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferBytes,
            FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        var line = new ArrayBufferWriter<byte>(ReadBufferBytes);
        var oversizedLines = 0;
        var discarding = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                var offset = 0;
                while (offset < read)
                {
                    var remaining = buffer.AsSpan(offset, read - offset);
                    var newlineIndex = remaining.IndexOf((byte)'\n');
                    var pieceLength = newlineIndex >= 0 ? newlineIndex : remaining.Length;
                    var piece = remaining[..pieceLength];

                    if (!discarding)
                    {
                        if (line.WrittenCount + piece.Length > MaxLineBytes)
                        {
                            oversizedLines++;
                            discarding = true;
                            line.Clear();
                        }
                        else
                        {
                            piece.CopyTo(line.GetSpan(piece.Length));
                            line.Advance(piece.Length);
                        }
                    }

                    offset += pieceLength;
                    if (newlineIndex < 0)
                        continue;

                    if (!discarding)
                        EmitLine(line, onLine);
                    line.Clear();
                    discarding = false;
                    offset++;
                }
            }

            if (!discarding && line.WrittenCount > 0)
                EmitLine(line, onLine);

            return oversizedLines;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EmitLine(
        ArrayBufferWriter<byte> line,
        Action<ReadOnlyMemory<byte>> onLine)
    {
        var length = line.WrittenCount;
        if (length > 0 && line.WrittenSpan[length - 1] == (byte)'\r')
            length--;
        if (length > 0)
            onLine(line.WrittenMemory[..length]);
    }
}
