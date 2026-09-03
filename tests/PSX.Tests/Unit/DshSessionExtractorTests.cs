using System.Diagnostics;
using System.Text;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DshSessionExtractorTests
{
    private sealed class RecordingSink : IDshExtractionEventSink
    {
        public List<DshExtractedHead> Heads { get; } = [];
        public List<DshExtractedUsage> Usages { get; } = [];
        public List<DshExtractedEof> Eofs { get; } = [];
        public List<DshExtractedError> Errors { get; } = [];

        public void OnHead(DshExtractedHead head) => Heads.Add(head);
        public void OnUsage(DshExtractedUsage usage) => Usages.Add(usage);
        public void OnEof(DshExtractedEof eof) => Eofs.Add(eof);
        public void OnError(DshExtractedError error) => Errors.Add(error);
    }

    private static bool Feed(
        NodeDshSessionExtractor.DshExtractionParser parser,
        string stdout,
        int fileCount,
        RecordingSink sink)
    {
        foreach (var rawLine in stdout.Split('\n'))
        {
            if (!parser.TryProcessLine(rawLine, fileCount, sink))
                return false;
        }

        return true;
    }

    [TestMethod]
    public void Parse_ValidOutput_ReadsEveryEventKindInOrder()
    {
        var stdout = string.Join('\n',
            """{"k":"hello","v":3}""",
            """{"k":"begin","file":0}""",
            """{"k":"head","file":0,"createdAt":1,"delegationDepth":0,"hasParent":true}""",
            """{"k":"usage","file":0,"kind":"assistant/chunk","t":123,"mid":"m1","turn":1,"step":2,"attempt":3,"seq":4,"in":1,"out":2,"cr":3,"cw":4}""",
            """{"k":"retry","file":0,"t":124,"turn":0,"step":1,"attempt":2}""",
            """{"k":"eof","file":0,"frames":2,"lines":5,"malformed":1,"truncated":true,"stray":7}""",
            """{"k":"begin","file":1}""",
            """{"k":"err","file":1,"code":"read_failed","detail":"ENOENT"}""");
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        var ok = Feed(parser, stdout, 2, sink);

        Assert.IsTrue(ok);
        Assert.AreEqual(3, parser.Hello!.ProtocolVersion);
        Assert.HasCount(1, sink.Heads);
        Assert.AreEqual(0, sink.Heads[0].File);
        Assert.IsTrue(sink.Heads[0].HasParent);
        Assert.HasCount(1, sink.Usages);
        var usage = sink.Usages[0];
        Assert.AreEqual(123, usage.TimestampMs);
        Assert.AreEqual("m1", usage.MessageId);
        Assert.AreEqual("assistant/chunk", usage.Kind);
        Assert.AreEqual(1, usage.Turn);
        Assert.AreEqual(2, usage.Step);
        Assert.AreEqual(3, usage.Attempt);
        Assert.AreEqual(4, usage.Seq);
        Assert.AreEqual(1, usage.InputTokens);
        Assert.AreEqual(2, usage.OutputTokens);
        Assert.AreEqual(3, usage.CacheReadTokens);
        Assert.AreEqual(4, usage.CacheCreationTokens);
        Assert.HasCount(1, sink.Eofs);
        Assert.IsTrue(sink.Eofs[0].Truncated);
        Assert.AreEqual(1, sink.Eofs[0].Malformed);
        Assert.AreEqual(7, sink.Eofs[0].Stray);
        Assert.HasCount(1, sink.Errors);
        Assert.AreEqual(1, sink.Errors[0].File);
        Assert.AreEqual("read_failed", sink.Errors[0].Code);
    }

    [TestMethod]
    public void Parse_MissingHello_ReturnsFalse()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();

        Assert.IsFalse(parser.TryProcessLine(
            """{"k":"usage","file":0,"t":1,"mid":"m","in":1}""", 1, new RecordingSink()));
        Assert.IsNull(parser.Hello);
    }

    [TestMethod]
    public void Parse_MalformedLine_ReturnsFalse()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(parser.TryProcessLine("""{"k":"hello","v":3}""", 1, sink));
        Assert.IsFalse(parser.TryProcessLine("{ not-json", 1, sink));
    }

    [TestMethod]
    public void Parse_DuplicateHello_ReturnsFalse()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(parser.TryProcessLine("""{"k":"hello","v":3}""", 1, sink));
        Assert.IsFalse(parser.TryProcessLine("""{"k":"hello","v":3}""", 1, sink));
    }

    [TestMethod]
    public void Parse_ProtocolVersionOne_IsRejected()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();

        Assert.IsFalse(parser.TryProcessLine("""{"k":"hello","v":1}""", 1, new RecordingSink()));
        Assert.IsNull(parser.Hello);
    }

    [TestMethod]
    public void Parse_ProtocolVersionTwo_IsRejected()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();

        Assert.IsFalse(parser.TryProcessLine("""{"k":"hello","v":2}""", 1, new RecordingSink()));
        Assert.IsNull(parser.Hello);
    }

    [TestMethod]
    public void Parse_ProtocolVersionFour_IsRejected()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();

        Assert.IsFalse(parser.TryProcessLine("""{"k":"hello","v":4}""", 1, new RecordingSink()));
        Assert.IsNull(parser.Hello);
    }

    [TestMethod]
    public void Parse_UnknownEventKinds_AreTolerated()
    {
        var stdout = string.Join('\n',
            """{"k":"hello","v":3}""",
            """{"k":"begin","file":0}""",
            """{"k":"retry","file":0,"t":1}""",
            """{"k":"future-kind","file":0}""",
            """{"k":"eof","file":0,"frames":1,"lines":1,"malformed":0,"truncated":false}""");
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(Feed(parser, stdout, 1, sink));
        Assert.HasCount(1, sink.Eofs);
    }

    [TestMethod]
    public void Parse_OutOfRangeFileIndex_SkipsTheEvent()
    {
        var stdout = string.Join('\n',
            """{"k":"hello","v":3}""",
            """{"k":"usage","file":7,"t":1,"mid":"m","in":5}""",
            """{"k":"eof","file":0,"frames":1,"lines":1,"malformed":0,"truncated":false}""");
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(Feed(parser, stdout, 2, sink));
        Assert.IsEmpty(sink.Usages);
        Assert.HasCount(1, sink.Eofs);
    }

    [TestMethod]
    public void Parse_UsageDefaultsMissingNumericFieldsToZero()
    {
        var stdout = string.Join('\n',
            """{"k":"hello","v":3}""",
            """{"k":"usage","file":0,"t":null,"mid":null}""");
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(Feed(parser, stdout, 1, sink));
        var usage = sink.Usages.Single();
        Assert.IsNull(usage.TimestampMs);
        Assert.IsNull(usage.MessageId);
        Assert.AreEqual(string.Empty, usage.Kind);
        Assert.IsNull(usage.Turn);
        Assert.IsNull(usage.Step);
        Assert.IsNull(usage.Attempt);
        Assert.IsNull(usage.Seq);
        Assert.AreEqual(0, usage.InputTokens);
        Assert.AreEqual(0, usage.OutputTokens);
        Assert.AreEqual(0, usage.CacheReadTokens);
        Assert.AreEqual(0, usage.CacheCreationTokens);
    }

    [TestMethod]
    public void Parse_OversizedLine_ReturnsFalse()
    {
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(parser.TryProcessLine("""{"k":"hello","v":3}""", 1, sink));
        Assert.IsFalse(parser.TryProcessLine(
            new string('x', NodeDshSessionExtractor.MaxOutputLineChars + 1), 1, sink));
    }

    [TestMethod]
    public void Parse_BlankLines_AreTolerated()
    {
        var stdout = "{\"k\":\"hello\",\"v\":3}\n\n{\"k\":\"eof\",\"file\":0,\"malformed\":0,\"truncated\":false}\n";
        var parser = new NodeDshSessionExtractor.DshExtractionParser();
        var sink = new RecordingSink();

        Assert.IsTrue(Feed(parser, stdout, 1, sink));
        Assert.HasCount(1, sink.Eofs);
    }

    [TestMethod]
    public void ExtractBatch_MissingNodeOrScript_ReturnsNull()
    {
        using var workspace = TestWorkspace.Create(nameof(ExtractBatch_MissingNodeOrScript_ReturnsNull));
        var existingFile = Path.Combine(workspace.Path, "not-node.exe");
        File.WriteAllBytes(existingFile, [0]);
        var sink = new RecordingSink();

        var noNode = new NodeDshSessionExtractor(() => null, () => existingFile);
        Assert.IsNull(noNode.ExtractBatch(["session.jsonl.zstd"], sink, CancellationToken.None));

        var missingNodeFile = new NodeDshSessionExtractor(
            () => Path.Combine(workspace.Path, "absent-node.exe"),
            () => existingFile);
        Assert.IsNull(missingNodeFile.ExtractBatch(["session.jsonl.zstd"], sink, CancellationToken.None));

        var noScript = new NodeDshSessionExtractor(() => existingFile, () => null);
        Assert.IsNull(noScript.ExtractBatch(["session.jsonl.zstd"], sink, CancellationToken.None));

        var missingScriptFile = new NodeDshSessionExtractor(
            () => existingFile,
            () => Path.Combine(workspace.Path, "absent-script.mjs"));
        Assert.IsNull(missingScriptFile.ExtractBatch(["session.jsonl.zstd"], sink, CancellationToken.None));
    }

    /// <summary>
    /// Process-level contract for the two shipped extractor behaviors: a
    /// retry marker without an explicit attempt stamps a synthetic attempt
    /// (starting at 1) onto the next same-(turn, step) usage event in a
    /// namespace distinct from an explicit attempt with the same number; an
    /// oversized skippable frame fails as
    /// a per-file too_large error so the rest of the batch still streams.
    /// Runs the real script under the gated Node 22 toolchain; skips when
    /// that toolchain is not provisioned.
    /// </summary>
    [TestMethod]
    public void ExtractBatch_RealNode_RetryStampsSyntheticAttemptAndOversizedFrameStaysPerFile()
    {
        var nodePath = Path.Combine(
            TestWorkspace.RepositoryRoot, "TestResults", "node22", "node-v22.23.1-win-x64", "node.exe");
        if (!File.Exists(nodePath))
            Assert.Inconclusive("Node 22 toolchain is not available under TestResults/node22.");
        var scriptPath = Path.Combine(
            TestWorkspace.RepositoryRoot, "tools", "usage-extractor", "dsh-session-extract.mjs");
        Assert.IsTrue(File.Exists(scriptPath), "The bundled extractor script must ship in the repository.");

        using var workspace = TestWorkspace.Create(
            nameof(ExtractBatch_RealNode_RetryStampsSyntheticAttemptAndOversizedFrameStaysPerFile));
        var retryFile = Path.Combine(workspace.Path, "retry.jsonl.zstd");
        var bombFile = Path.Combine(workspace.Path, "bomb.jsonl.zstd");
        RunNodeGenerator(nodePath, retryFile, bombFile);

        // The oversized-skippable-frame file leads the batch so the retry
        // fixture's events prove the scan continued after the per-file error.
        var extractor = new NodeDshSessionExtractor(() => nodePath, () => scriptPath);
        var sink = new RecordingSink();
        var hello = extractor.ExtractBatch([bombFile, retryFile], sink, CancellationToken.None);

        Assert.IsNotNull(hello);
        Assert.AreEqual(3, hello!.ProtocolVersion);

        Assert.HasCount(1, sink.Errors);
        Assert.AreEqual(0, sink.Errors[0].File);
        Assert.AreEqual("too_large", sink.Errors[0].Code);

        Assert.HasCount(2, sink.Usages);
        foreach (var usage in sink.Usages)
        {
            Assert.AreEqual(1, usage.File);
            Assert.AreEqual("m1", usage.MessageId);
            Assert.AreEqual(1, usage.Turn);
            Assert.AreEqual(1, usage.Step);
        }

        Assert.AreEqual(1, sink.Usages[0].Attempt);
        Assert.IsFalse(sink.Usages[0].AttemptSynthetic);
        Assert.AreEqual(10, sink.Usages[0].InputTokens);
        Assert.AreEqual(1, sink.Usages[1].Attempt);
        Assert.IsTrue(sink.Usages[1].AttemptSynthetic);
        Assert.AreEqual(7, sink.Usages[1].InputTokens);

        Assert.HasCount(1, sink.Eofs);
        Assert.AreEqual(1, sink.Eofs[0].File);
        Assert.IsFalse(sink.Eofs[0].Truncated);
        Assert.AreEqual(0, sink.Eofs[0].Malformed);
        Assert.AreEqual(0, sink.Eofs[0].Stray);
    }

    private static void RunNodeGenerator(string nodePath, string retryFile, string bombFile)
    {
        const string Generator =
            "const z = require('node:zlib');" +
            "const fs = require('node:fs');" +
            "const [retryPath, bombPath] = process.argv.slice(1);" +
            "const u1 = '{\"type\":\"assistant/message\",\"time\":1784522400000," +
            "\"data\":{\"turn\":1,\"step\":1,\"attempt\":1,\"message\":{\"id\":\"m1\"}," +
            "\"usage\":{\"inputTokens\":10,\"outputTokens\":5}}}';" +
            "const r = '{\"type\":\"agent.retry\",\"time\":1784522401000," +
            "\"data\":{\"turn\":1,\"step\":1}}';" +
            "const u2 = '{\"type\":\"assistant/message\",\"time\":1784522402000," +
            "\"data\":{\"turn\":1,\"step\":1,\"message\":{\"id\":\"m1\"}," +
            "\"usage\":{\"inputTokens\":7,\"outputTokens\":3}}}';" +
            "const frame1 = z.zstdCompressSync(Buffer.from(u1 + '\\n' + r + '\\n'));" +
            "const frame2 = z.zstdCompressSync(Buffer.from(u2 + '\\n'));" +
            "fs.writeFileSync(retryPath, Buffer.concat([frame1, frame2]));" +
            "const bomb = Buffer.alloc(8);" +
            "bomb.writeUInt32LE(0x184d2a50, 0);" +
            "bomb.writeUInt32LE(0xffffffff, 4);" +
            "fs.writeFileSync(bombPath, Buffer.concat([bomb, Buffer.from('junk')]));";

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(Generator);
        startInfo.ArgumentList.Add(retryFile);
        startInfo.ArgumentList.Add(bombFile);
        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        using (process)
        {
            Assert.IsTrue(
                process!.WaitForExit(30_000),
                "The Node fixture generator timed out: " + process.StandardError.ReadToEnd());
            Assert.AreEqual(
                0,
                process.ExitCode,
                $"Node fixture generator failed:{Environment.NewLine}{process.StandardError.ReadToEnd()}");
        }
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class BoundedLineReaderTests
{
    private static async Task<(bool Ok, List<string> Lines)> Read(
        string content,
        int maxLineChars)
    {
        var lines = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var ok = await BoundedLineReader.ReadLinesAsync(
            stream, maxLineChars, line =>
            {
                lines.Add(line);
                return true;
            },
            CancellationToken.None);
        return (ok, lines);
    }

    [TestMethod]
    public async Task Read_LfAndCrlf_DeliversLinesWithoutTerminators()
    {
        var (ok, lines) = await Read("one\ntwo\r\nthree", 100);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, lines);
    }

    [TestMethod]
    public async Task Read_LineExactlyAtTheCap_IsDelivered()
    {
        var line = new string('x', 64);

        var (ok, lines) = await Read(line + "\nnext", 64);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { line, "next" }, lines);
    }

    [TestMethod]
    public async Task Read_LineOverTheCap_AbortsWithoutDeliveringIt()
    {
        var (ok, lines) = await Read(new string('x', 65) + "\nnever", 64);

        Assert.IsFalse(ok);
        Assert.IsEmpty(lines);
    }

    [TestMethod]
    public async Task Read_UnterminatedTailAtTheCap_IsDelivered()
    {
        var line = new string('y', 64);

        var (ok, lines) = await Read(line, 64);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { line }, lines);
    }

    [TestMethod]
    public async Task Read_UnterminatedTailOverTheCap_Aborts()
    {
        var (ok, _) = await Read(new string('y', 65), 64);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task Read_MultiByteUtf8AcrossBufferBoundary_StaysIntact()
    {
        // Push a multi-byte sequence across the 64 KiB read boundary.
        var padding = new string('a', 64 * 1024 - 1);
        var content = padding + "\né水🙂\n" + "done\n";
        var expected = new[] { padding, "é水🙂", "done" };

        var (ok, lines) = await Read(content, 128 * 1024);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(expected, lines);
    }

    [TestMethod]
    public async Task Read_CallbackFalse_AbortsTheRead()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("a\nb\nc\n"));
        var lines = new List<string>();

        var ok = await BoundedLineReader.ReadLinesAsync(
            stream, 100, line =>
            {
                lines.Add(line);
                return lines.Count < 2;
            },
            CancellationToken.None);

        Assert.IsFalse(ok);
        CollectionAssert.AreEqual(new[] { "a", "b" }, lines);
    }
}
