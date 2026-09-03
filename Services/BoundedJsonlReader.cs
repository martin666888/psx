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
        return Read(path, (line, _) => onLine(line), cancellationToken);
    }

    /// <summary>
    /// Reads like <see cref="Read(string, Action{ReadOnlyMemory{byte}}, CancellationToken)"/>
    /// but also tells the callback whether the emitted line is the unterminated
    /// final piece of the file (no trailing newline). Callers can tolerate a
    /// half-written trailing line that an interrupted append may have left.
    /// </summary>
    public static int Read(
        string path,
        Action<ReadOnlyMemory<byte>, bool> onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return Read(path, 0, (line, isUnterminatedFinalLine, _, _) =>
            onLine(line, isUnterminatedFinalLine), cancellationToken);
    }

    /// <summary>
    /// Reads like <see cref="Read(string, Action{ReadOnlyMemory{byte}, bool}, CancellationToken)"/>
    /// starting at an absolute byte offset, and additionally reports each
    /// line's absolute start offset and the raw byte count it occupies in the
    /// file, terminator included. Incremental readers use the offset/length to
    /// verify and resume exactly after the last consumed line.
    /// </summary>
    public static int Read(
        string path,
        long startOffset,
        Action<ReadOnlyMemory<byte>, bool, long, int> onLine,
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
        if (startOffset > 0)
            stream.Seek(startOffset, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        var line = new ArrayBufferWriter<byte>(ReadBufferBytes);
        var oversizedLines = 0;
        var discarding = false;
        var chunkBase = startOffset;
        var lineStart = startOffset;

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

                    var newlineAbsolute = chunkBase + offset;
                    if (!discarding)
                    {
                        EmitLine(
                            line,
                            isUnterminatedFinalLine: false,
                            lineStart,
                            checked((int)(newlineAbsolute - lineStart + 1)),
                            onLine);
                    }

                    line.Clear();
                    discarding = false;
                    offset++;
                    lineStart = newlineAbsolute + 1;
                }

                chunkBase += read;
            }

            if (!discarding && line.WrittenCount > 0)
            {
                EmitLine(
                    line,
                    isUnterminatedFinalLine: true,
                    lineStart,
                    checked((int)(stream.Position - lineStart)),
                    onLine);
            }

            return oversizedLines;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EmitLine(
        ArrayBufferWriter<byte> line,
        bool isUnterminatedFinalLine,
        long lineStart,
        int rawLength,
        Action<ReadOnlyMemory<byte>, bool, long, int> onLine)
    {
        var length = line.WrittenCount;
        if (length > 0 && line.WrittenSpan[length - 1] == (byte)'\r')
            length--;
        if (length > 0)
            onLine(line.WrittenMemory[..length], isUnterminatedFinalLine, lineStart, rawLength);
    }
}
