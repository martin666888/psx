using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PSX.Services;

/// <summary>Session-head facts for one file in an extraction batch. Only the
/// fork marker is consumed; createdAt/delegationDepth stay on the wire.</summary>
public sealed record DshExtractedHead(int File, bool HasParent);

/// <summary>One usage sample from a source event carrying data.usage (final
/// assistant/message and streaming chunks alike). <see cref="TimestampMs"/> and
/// <see cref="MessageId"/> may be null when the source event lacked them;
/// turn/step/attempt/seq describe the attempt the sample belongs to.</summary>
public sealed record DshExtractedUsage(
    int File,
    long? TimestampMs,
    string? MessageId,
    string Kind,
    long? Turn,
    long? Step,
    long? Attempt,
    long? Seq,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    bool AttemptSynthetic = false);

/// <summary>End-of-file marker for one input file. A truncated tail means the
/// final zstd frame was incomplete; a non-zero malformed count means lines or
/// records were skipped; a non-zero stray count means garbage bytes sat outside
/// any valid frame. Events already emitted remain valid.</summary>
public sealed record DshExtractedEof(int File, bool Truncated, int Malformed, int Stray);

/// <summary>Per-file failure. The batch continues with the remaining files.</summary>
public sealed record DshExtractedError(int File, string Code);

/// <summary>Successful batch header: the negotiated extractor protocol
/// version. A null batch result means the whole batch is unavailable.</summary>
public sealed record DshExtractionHello(int ProtocolVersion);

/// <summary>
/// Receives typed extraction events as they stream off the extractor's
/// stdout. Events for one file are contiguous (begin..eof); retry markers are
/// tolerated and never surface. Implementations must not throw.
/// </summary>
public interface IDshExtractionEventSink
{
    void OnHead(DshExtractedHead head);

    void OnUsage(DshExtractedUsage usage);

    void OnEof(DshExtractedEof eof);

    void OnError(DshExtractedError error);
}

/// <summary>
/// Extracts usage events from a batch of DSH session.jsonl.zstd files,
/// streaming typed events to <paramref name="sink"/> as they are parsed. A
/// null result means the whole batch is unavailable (missing Node, missing
/// script, process failure or protocol violation) and maps to
/// extractor_unavailable; individual file failures arrive as
/// <see cref="DshExtractedError"/> events. The payload only ever carries token
/// counts, timestamps, message ids and attempt coordinates — never paths,
/// titles, cwd or conversation content. Implementations never throw except
/// for cancellation.
/// </summary>
public interface IDshSessionExtractor
{
    DshExtractionHello? ExtractBatch(
        IReadOnlyList<string> filePaths,
        IDshExtractionEventSink sink,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDshSessionExtractor"/> backed by the packaged portable Node
/// running the bundled extractor script (one process per batch: paths on
/// stdin, line-delimited JSON events on stdout). stdout is consumed through
/// <see cref="BoundedLineReader"/>, so memory stays bounded by the fixed read
/// buffer plus the 1 MiB line cap, never by batch size or line length; a line
/// past the cap is a protocol violation and invalidates the whole batch.
/// stderr is drained concurrently and retained only for diagnostics.
/// Cancellation and timeout reap the process tree and drain the pipes through
/// <see cref="RuntimeProcessCleanup"/> before returning.
/// </summary>
public sealed class NodeDshSessionExtractor : IDshSessionExtractor
{
    internal const int MaxOutputLineChars = 1024 * 1024;

    private const int MaxStderrDiagnosticChars = 64 * 1024;
    private const int SupportedProtocolVersion = 3;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<string?> _nodePathResolver;
    private readonly Func<string?> _scriptPathResolver;
    private readonly TimeSpan _timeout;

    public NodeDshSessionExtractor(
        Func<string?> nodePathResolver,
        Func<string?> scriptPathResolver,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(nodePathResolver);
        ArgumentNullException.ThrowIfNull(scriptPathResolver);
        _nodePathResolver = nodePathResolver;
        _scriptPathResolver = scriptPathResolver;
        _timeout = timeout ?? DefaultTimeout;
    }

    public DshExtractionHello? ExtractBatch(
        IReadOnlyList<string> filePaths,
        IDshExtractionEventSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(sink);
        var nodePath = _nodePathResolver();
        var scriptPath = _scriptPathResolver();
        if (string.IsNullOrWhiteSpace(nodePath) || !File.Exists(nodePath))
            return null;
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            return null;

        return RunAsync(nodePath, scriptPath, filePaths, sink, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<DshExtractionHello?> RunAsync(
        string nodePath,
        string scriptPath,
        IReadOnlyList<string> filePaths,
        IDshExtractionEventSink sink,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(scriptPath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception)
        {
            return null;
        }

        if (process == null)
            return null;
        using var processLifetime = process;

        // stderr is drained concurrently so a chatty extractor cannot deadlock
        // on a full pipe; only a bounded head is retained for diagnostics.
        var stderrTask = DrainStderrAsync(process.StandardError);
        var stdinTask = WritePathsAsync(process, filePaths, close: true);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var parser = new DshExtractionParser();
        try
        {
            var linesValid = await BoundedLineReader.ReadLinesAsync(
                    process.StandardOutput.BaseStream,
                    MaxOutputLineChars,
                    line => parser.TryProcessLine(line, filePaths.Count, sink),
                    timeoutCts.Token)
                .ConfigureAwait(false);
            if (!linesValid)
            {
                await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdinTask, stderrTask)
                    .ConfigureAwait(false);
                return null;
            }

            await stdinTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await stderrTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdinTask, stderrTask)
                .ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdinTask, stderrTask)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A pipe broke because the process died early; reap before reporting.
            await RuntimeProcessCleanup.TerminateAndDrainAsync(process, stdinTask, stderrTask)
                .ConfigureAwait(false);
            return null;
        }

        if (process.ExitCode != 0)
            return null;

        return parser.Hello;
    }

    private static async Task WritePathsAsync(
        Process process,
        IReadOnlyList<string> filePaths,
        bool close)
    {
        foreach (var path in filePaths)
            await process.StandardInput.WriteLineAsync(path).ConfigureAwait(false);
        if (close)
            process.StandardInput.Close();
    }

    private static async Task<string> DrainStderrAsync(StreamReader stderr)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await stderr.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
                break;
            var remaining = MaxStderrDiagnosticChars - retained.Length;
            if (remaining > 0)
                retained.Append(buffer, 0, Math.Min(read, remaining));
        }

        return retained.ToString();
    }

    /// <summary>
    /// Stateful parser for the extractor's line-delimited stdout (protocol
    /// v3). The first event must be the hello header with <c>v == 3</c>;
    /// malformed or oversized lines, a missing or repeated hello and a wrong
    /// version invalidate the whole batch. begin/retry and unknown event kinds
    /// are tolerated, and events with an out-of-range file index are skipped.
    /// </summary>
    internal sealed class DshExtractionParser
    {
        private bool _helloSeen;

        public DshExtractionHello? Hello { get; private set; }

        public bool TryProcessLine(string line, int fileCount, IDshExtractionEventSink sink)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                return true;
            if (trimmed.Length > MaxOutputLineChars)
                return false;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("k", out var kindElement)
                    || kindElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                switch (kindElement.GetString())
                {
                    case "hello":
                        if (_helloSeen
                            || !TryGetInt64(root, "v", out var version)
                            || version != SupportedProtocolVersion)
                        {
                            return false;
                        }

                        _helloSeen = true;
                        Hello = new DshExtractionHello((int)version);
                        return true;
                    case "head":
                        if (!_helloSeen)
                            return false;
                        if (!TryGetFileIndex(root, fileCount, out var headFile))
                            return true;
                        sink.OnHead(new DshExtractedHead(headFile, GetBoolean(root, "hasParent")));
                        return true;
                    case "usage":
                        if (!_helloSeen)
                            return false;
                        if (!TryGetFileIndex(root, fileCount, out var usageFile))
                            return true;
                        sink.OnUsage(new DshExtractedUsage(
                            usageFile,
                            GetInt64OrNull(root, "t"),
                            GetStringOrNull(root, "mid"),
                            GetStringOrNull(root, "kind") ?? string.Empty,
                            GetInt64OrNull(root, "turn"),
                            GetInt64OrNull(root, "step"),
                            GetInt64OrNull(root, "attempt"),
                            GetInt64OrNull(root, "seq"),
                            GetInt64OrDefault(root, "in"),
                            GetInt64OrDefault(root, "out"),
                            GetInt64OrDefault(root, "cr"),
                            GetInt64OrDefault(root, "cw"),
                            GetBoolean(root, "attemptSynthetic")));
                        return true;
                    case "eof":
                        if (!_helloSeen)
                            return false;
                        if (!TryGetFileIndex(root, fileCount, out var eofFile))
                            return true;
                        sink.OnEof(new DshExtractedEof(
                            eofFile,
                            GetBoolean(root, "truncated"),
                            (int)GetInt64OrDefault(root, "malformed"),
                            (int)GetInt64OrDefault(root, "stray")));
                        return true;
                    case "err":
                        if (!_helloSeen)
                            return false;
                        if (!TryGetFileIndex(root, fileCount, out var errorFile))
                            return true;
                        sink.OnError(new DshExtractedError(
                            errorFile,
                            GetStringOrNull(root, "code") ?? "extractor_error"));
                        return true;
                    default:
                        // begin, retry and future event kinds need no action.
                        return _helloSeen;
                }
            }
        }

        private static bool TryGetFileIndex(JsonElement root, int fileCount, out int file)
        {
            file = 0;
            if (!TryGetInt64(root, "file", out var value) || value < 0 || value >= fileCount)
                return false;
            file = (int)value;
            return true;
        }

        private static bool TryGetInt64(JsonElement root, string name, out long value)
        {
            value = 0;
            return root.TryGetProperty(name, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt64(out value);
        }

        private static long? GetInt64OrNull(JsonElement root, string name) =>
            TryGetInt64(root, name, out var value) ? value : null;

        private static long GetInt64OrDefault(JsonElement root, string name) =>
            TryGetInt64(root, name, out var value) ? value : 0;

        private static bool GetBoolean(JsonElement root, string name) =>
            root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.True;

        private static string? GetStringOrNull(JsonElement root, string name) =>
            root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
    }
}
