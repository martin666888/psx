using System.Diagnostics;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DshLocalUsageContributorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // 2026-07-20T04:40:00Z.
    private const long BaseTime = 1784522400000;

    private sealed class RecordingSink : IAgentUsageRecordSink
    {
        public List<AgentUsageRecord> Records { get; } = [];
        public void Add(AgentUsageRecord record) => Records.Add(record);
    }

    private sealed record CollectResult(
        AgentUsageSourceStatus Status,
        List<AgentUsageRecord> Records)
    {
        public long TotalTokens => Records.Sum(record =>
            record.InputTokens
            + record.OutputTokens
            + record.CacheReadTokens
            + record.CacheCreationTokens);
    }

    private sealed class FileOutcome
    {
        public bool HasParent { get; init; }
        public bool Truncated { get; init; }
        public int Malformed { get; init; }
        public int Stray { get; init; }
        public string? ErrorCode { get; init; }
        public List<DshExtractedUsage> Usages { get; } = [];
    }

    /// <summary>In-memory cache spy with an isolated batch namespace.</summary>
    private sealed class RecordingCacheStore : IAgentUsageCacheStore
    {
        private readonly Dictionary<string, AgentUsageCacheEntry> _entries = new(StringComparer.Ordinal);

        public List<string> SavedIds { get; } = [];
        public List<string> DeletedIds { get; } = [];
        public List<string> StagedIds { get; } = [];
        public List<string> CommittedIds { get; } = [];

        public AgentUsageCacheEntry? Get(string entryId) =>
            _entries.TryGetValue(entryId, out var entry) ? entry : null;

        public void Save(string entryId, AgentUsageCacheEntry entry)
        {
            SavedIds.Add(entryId);
            _entries[entryId] = entry;
        }

        public void Delete(string entryId)
        {
            DeletedIds.Add(entryId);
            _entries.Remove(entryId);
        }

        public IAgentUsageCacheBatch BeginBatch() => new RecordingBatch(this);

        private sealed class RecordingBatch(RecordingCacheStore owner) : IAgentUsageCacheBatch
        {
            private readonly Dictionary<string, AgentUsageCacheEntry> _staged = new(StringComparer.Ordinal);
            private bool _finished;

            public void Save(string entryId, AgentUsageCacheEntry entry)
            {
                if (_finished)
                    return;
                owner.StagedIds.Add(entryId);
                _staged[entryId] = entry;
            }

            public void Commit()
            {
                if (_finished)
                    return;
                _finished = true;
                foreach (var (entryId, entry) in _staged)
                {
                    owner._entries[entryId] = entry;
                    owner.CommittedIds.Add(entryId);
                }
            }

            public void Dispose() => _finished = true;
        }
    }

    /// <summary>Programmable extractor: a null hello models a batch-level
    /// outage (missing Node), per-path outcomes model per-file event
    /// streams. <see cref="FailAfterDispatch"/> models a protocol violation
    /// discovered after valid events already streamed.</summary>
    private sealed class StubExtractor : IDshSessionExtractor
    {
        public bool Unavailable { get; set; }
        public bool FailRealBatches { get; set; }
        public bool FailAfterDispatch { get; set; }
        public int ProtocolVersion { get; set; } = 3;
        public int ProbeCount { get; private set; }
        public List<string[]> Batches { get; } = [];
        public Dictionary<string, FileOutcome> Outcomes { get; } = new(StringComparer.Ordinal);

        public DshExtractionHello? ExtractBatch(
            IReadOnlyList<string> filePaths,
            IDshExtractionEventSink sink,
            CancellationToken cancellationToken)
        {
            if (Unavailable)
                return null;
            if (filePaths.Count == 0)
            {
                ProbeCount++;
                return new DshExtractionHello(ProtocolVersion);
            }

            Batches.Add(filePaths.ToArray());
            if (FailRealBatches)
                return null;
            for (var i = 0; i < filePaths.Count; i++)
            {
                if (!Outcomes.TryGetValue(filePaths[i], out var outcome))
                {
                    sink.OnEof(new DshExtractedEof(i, false, 0, 0));
                    continue;
                }

                if (outcome.ErrorCode != null)
                {
                    sink.OnError(new DshExtractedError(i, outcome.ErrorCode));
                    continue;
                }

                if (outcome.HasParent)
                    sink.OnHead(new DshExtractedHead(i, true));
                foreach (var usage in outcome.Usages)
                    sink.OnUsage(usage with { File = i });
                sink.OnEof(new DshExtractedEof(i, outcome.Truncated, outcome.Malformed, outcome.Stray));
            }

            return FailAfterDispatch ? null : new DshExtractionHello(ProtocolVersion);
        }
    }

    private static DshExtractedUsage Usage(
        long? timestamp,
        string? mid,
        long input,
        long output = 0,
        long cacheRead = 0,
        long cacheCreation = 0,
        string kind = "assistant/message",
        long? turn = null,
        long? step = null,
        long? attempt = null,
        long? seq = null,
        bool attemptSynthetic = false) =>
        new(0, timestamp, mid, kind, turn, step, attempt, seq,
            input, output, cacheRead, cacheCreation, attemptSynthetic);

    private static string WriteSessionFile(
        string home,
        string projectDir,
        string sessionId,
        byte payload = 1)
    {
        var path = Path.Combine(
            home, "sessions", projectDir, sessionId, "session.jsonl.zstd");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(payload, 16).ToArray());
        return path;
    }

    private static CollectResult Collect(
        string home,
        StubExtractor extractor,
        IAgentUsageCacheStore? cache = null,
        TimeZoneInfo? zone = null,
        List<(string Path, DshSessionScanMode Mode)>? scanLog = null)
    {
        var contributor = new DshLocalUsageContributor(
            () => home,
            extractor,
            cache,
            zone ?? Utc,
            () => Path.Combine(home, ".fold-spill"));
        if (scanLog != null)
            contributor.ScanObserver = (path, mode) => scanLog.Add((path, mode));
        var sink = new RecordingSink();
        var status = contributor.Collect(sink, CancellationToken.None);
        return new CollectResult(status, sink.Records);
    }

    [TestMethod]
    public void Descriptor_ExposesDshIdentity()
    {
        var contributor = new DshLocalUsageContributor(() => null);

        Assert.AreEqual("local-dsh", contributor.Descriptor.Key);
        Assert.AreEqual("DeepSeek Harness", contributor.Descriptor.DisplayName);
        Assert.AreEqual("dsh", contributor.Descriptor.IconKey);
        Assert.IsNull(contributor.Descriptor.TrackedProviderKey);
    }

    [TestMethod]
    public void Collect_MissingSessionsDirectory_IsAvailableZeroEvenWithoutExtractor()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingSessionsDirectory_IsAvailableZeroEvenWithoutExtractor));
        var extractor = new StubExtractor { Unavailable = true };

        var result = Collect(Path.Combine(workspace.Path, "no-such-home"), extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(0, result.Status.ExpectedSessions);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Status.Reasons);
        Assert.IsEmpty(result.Records);
        Assert.AreEqual(0, extractor.ProbeCount);
    }

    [TestMethod]
    public void Collect_EmptySessionsDirectory_IsAvailableZero()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_EmptySessionsDirectory_IsAvailableZero));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "sessions"));
        var extractor = new StubExtractor { Unavailable = true };

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(0, result.Status.ExpectedSessions);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_UsageEvents_SumAllFourTokenFieldsPerDay()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UsageEvents_SumAllFourTokenFieldsPerDay));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        var nextDay = new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 10, 20, 30, 40));
        extractor.Outcomes[file].Usages.Add(Usage(nextDay, "m2", 1, 2, 3, 4));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(110, result.TotalTokens);
        // One synthetic record per day; the day total rides on InputTokens.
        Assert.HasCount(2, result.Records);
        Assert.IsTrue(result.Records.Any(record => record.InputTokens == 100));
        Assert.IsTrue(result.Records.Any(record => record.InputTokens == 10));
    }

    [TestMethod]
    public void Collect_ChunkThenFinalSameAttempt_CountsOnlyTheFinal()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ChunkThenFinalSameAttempt_CountsOnlyTheFinal));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, null, 100, kind: "assistant/chunk", turn: 1, step: 1, attempt: 1, seq: 7));
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime + 1000, null, 40, kind: "assistant/message", turn: 1, step: 1, attempt: 1));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(40, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_StreamingSamples_FinalSamplePerMessageIdWins()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_StreamingSamples_FinalSamplePerMessageIdWins));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, "m1", 100, kind: "assistant/chunk", seq: 3));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, "m1", 40));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(40, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_ChunkOnlyAttempt_CountsTheHighestSeqChunk()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ChunkOnlyAttempt_CountsTheHighestSeqChunk));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, null, 25, kind: "assistant/chunk", turn: 1, step: 1, attempt: 1, seq: 2));
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, null, 10, kind: "assistant/chunk", turn: 1, step: 1, attempt: 1, seq: 1));
        // A null-seq chunk sorts lowest even when it arrives last.
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, null, 99, kind: "assistant/chunk", turn: 1, step: 1, attempt: 1));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(25, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_TwoAttemptsOfSameTurnStep_BothAccumulate()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_TwoAttemptsOfSameTurnStep_BothAccumulate));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, null, 10, turn: 1, step: 1, attempt: 1));
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime + 1000, null, 20, turn: 1, step: 1, attempt: 2));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(30, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_ResidualEvents_AreCountedIndividuallyWithoutDedup()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ResidualEvents_AreCountedIndividuallyWithoutDedup));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        // Neither message id nor turn/step: non-dedupable, each event counts.
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, null, 10));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, null, 20));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(30, result.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
    }

    [TestMethod]
    public void Collect_NullTimestamp_CountsBadLineAndKeepsOtherEvents()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NullTimestamp_CountsBadLineAndKeepsOtherEvents));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(null, "m1", 500));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m2", 10));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.BadLines);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(10, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_MalformedEof_CountsBadLinesAndStaysUncached()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MalformedEof_CountsBadLinesAndStaysUncached));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "damaged");
        var extractor = new StubExtractor();
        // The extractor dropped invalid-token records before they reached C#;
        // eof.malformed carries that count. Well-formed events still count.
        extractor.Outcomes[file] = new FileOutcome { Malformed = 3 };
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 21));

        var result = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(3, result.Status.BadLines);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(21, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(file)));
    }

    [TestMethod]
    public void Collect_SharedMessageIdAcrossFiles_CountsOnceAndFlagsLineage()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SharedMessageIdAcrossFiles_CountsOnceAndFlagsLineage));
        var fileA = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var fileB = WriteSessionFile(workspace.Path, "proj-a", "session-b");
        var extractor = new StubExtractor();
        extractor.Outcomes[fileA] = new FileOutcome();
        extractor.Outcomes[fileA].Usages.Add(Usage(BaseTime, "shared", 100));
        extractor.Outcomes[fileB] = new FileOutcome();
        extractor.Outcomes[fileB].Usages.Add(Usage(BaseTime, "shared", 100));
        extractor.Outcomes[fileB].Usages.Add(Usage(BaseTime, "own", 5));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(2, result.Status.MatchedSessions);
        Assert.AreEqual(105, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_CachedParentPlusFreshFork_CountsSharedIdOnce()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CachedParentPlusFreshFork_CountsSharedIdOnce));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var parent = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[parent] = new FileOutcome();
        extractor.Outcomes[parent].Usages.Add(Usage(BaseTime, "shared", 100));
        var first = Collect(workspace.Path, extractor, cache);
        Assert.AreEqual(100, first.TotalTokens);

        // The fork arrives after the parent was fully cached.
        var fork = WriteSessionFile(workspace.Path, "proj-a", "session-b", payload: 2);
        extractor.Outcomes[fork] = new FileOutcome();
        extractor.Outcomes[fork].Usages.Add(Usage(BaseTime, "shared", 100));
        extractor.Outcomes[fork].Usages.Add(Usage(BaseTime, "own", 5));
        var scanLog = new List<(string Path, DshSessionScanMode Mode)>();
        var second = Collect(workspace.Path, extractor, cache, scanLog: scanLog);

        Assert.AreEqual(DshSessionScanMode.CacheHit, scanLog[0].Mode);
        Assert.AreEqual(DshSessionScanMode.Extract, scanLog[1].Mode);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, second.Status.Status);
        CollectionAssert.Contains(
            second.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(105, second.TotalTokens);
    }

    [TestMethod]
    public void Collect_DeletedParent_RestoresChildAttribution()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DeletedParent_RestoresChildAttribution));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var parent = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var child = WriteSessionFile(workspace.Path, "proj-a", "session-b", payload: 2);
        var extractor = new StubExtractor();
        extractor.Outcomes[parent] = new FileOutcome();
        extractor.Outcomes[parent].Usages.Add(Usage(BaseTime, "shared", 100));
        extractor.Outcomes[child] = new FileOutcome();
        extractor.Outcomes[child].Usages.Add(Usage(BaseTime, "shared", 100));
        var first = Collect(workspace.Path, extractor, cache);
        Assert.AreEqual(100, first.TotalTokens);
        Assert.AreEqual(2, first.Status.MatchedSessions);

        // The parent's cache entry no longer suppresses the child's copy.
        Directory.Delete(Path.GetDirectoryName(parent)!, recursive: true);
        var second = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(100, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
        Assert.AreEqual(1, second.Status.MatchedSessions);
    }

    [TestMethod]
    public void Collect_HasParentHead_CountsEventsAndFlagsLineage()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_HasParentHead_CountsEventsAndFlagsLineage));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "fork-session");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome { HasParent = true };
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 42));
        var scanLog = new List<(string Path, DshSessionScanMode Mode)>();

        var first = Collect(workspace.Path, extractor, cache, scanLog: scanLog);
        var second = Collect(workspace.Path, extractor, cache, scanLog: scanLog);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, first.Status.Status);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, second.Status.Status);
        CollectionAssert.Contains(
            second.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(DshSessionScanMode.CacheHit, scanLog[1].Mode);
        Assert.AreEqual(1, second.Status.MatchedSessions);
        Assert.AreEqual(42, second.TotalTokens);
    }

    [TestMethod]
    public void Collect_PerFileError_DoesNotAbortOtherFiles()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_PerFileError_DoesNotAbortOtherFiles));
        var broken = WriteSessionFile(workspace.Path, "proj-a", "broken");
        var healthy = WriteSessionFile(workspace.Path, "proj-a", "healthy");
        var extractor = new StubExtractor();
        extractor.Outcomes[broken] = new FileOutcome { ErrorCode = "read_failed" };
        extractor.Outcomes[healthy] = new FileOutcome();
        extractor.Outcomes[healthy].Usages.Add(Usage(BaseTime, "m1", 33));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(2, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(1, result.Status.SkippedFiles);
        Assert.AreEqual(33, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_BadFormatError_ReportsUnsupportedFormat()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_BadFormatError_ReportsUnsupportedFormat));
        var file = WriteSessionFile(workspace.Path, "proj-a", "not-zstd");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome { ErrorCode = "bad_format" };

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnsupportedFormat);
        CollectionAssert.DoesNotContain(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
        Assert.AreEqual(0, result.Status.MatchedSessions);
    }

    [TestMethod]
    public void Collect_TruncatedTail_KeepsEventsAndStaysUncached()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_TruncatedTail_KeepsEventsAndStaysUncached));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "still-writing");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome { Truncated = true };
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 21));

        var result = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.SkippedFiles);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(21, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(file)));
    }

    [TestMethod]
    public void Collect_ExtractorUnavailable_WithNoUsableData_IsUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExtractorUnavailable_WithNoUsableData_IsUnavailable));
        WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor { Unavailable = true };

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.ExtractorUnavailable);
    }

    [TestMethod]
    public void Collect_ExtractorUnavailable_CachedFilesStillContributeAsPartial()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExtractorUnavailable_CachedFilesStillContributeAsPartial));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var cachedFile = WriteSessionFile(workspace.Path, "proj-a", "cached");
        var extractor = new StubExtractor();
        extractor.Outcomes[cachedFile] = new FileOutcome();
        extractor.Outcomes[cachedFile].Usages.Add(Usage(BaseTime, "m1", 77));
        var first = Collect(workspace.Path, extractor, cache);
        Assert.AreEqual(77, first.TotalTokens);

        // Node disappears; a second file shows up that can no longer parse.
        WriteSessionFile(workspace.Path, "proj-a", "fresh");
        extractor.Unavailable = true;
        var second = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, second.Status.Status);
        CollectionAssert.Contains(
            second.Status.Reasons.ToArray(),
            AgentUsageGapReason.ExtractorUnavailable);
        Assert.AreEqual(2, second.Status.ExpectedSessions);
        Assert.AreEqual(1, second.Status.MatchedSessions);
        Assert.AreEqual(77, second.TotalTokens);
        // The outage scan never started a real batch; only the probe ran.
        Assert.HasCount(1, extractor.Batches);
    }

    [TestMethod]
    public void Collect_UnchangedFile_UsesCacheWithoutReextraction()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnchangedFile_UsesCacheWithoutReextraction));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "cached");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 55));
        var scanLog = new List<(string Path, DshSessionScanMode Mode)>();

        var first = Collect(workspace.Path, extractor, cache, scanLog: scanLog);
        Assert.HasCount(1, extractor.Batches);

        var second = Collect(workspace.Path, extractor, cache, scanLog: scanLog);

        // The second scan only probed the protocol version; no file batch ran.
        Assert.HasCount(1, extractor.Batches);
        Assert.AreEqual(2, extractor.ProbeCount);
        Assert.AreEqual(DshSessionScanMode.Extract, scanLog[0].Mode);
        Assert.AreEqual(DshSessionScanMode.CacheHit, scanLog[1].Mode);
        Assert.AreEqual(first.TotalTokens, second.TotalTokens);
        Assert.AreEqual(55, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_ChangedFile_ReextractsAndReplacesContribution()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ChangedFile_ReextractsAndReplacesContribution));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "changing");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 100));
        var first = Collect(workspace.Path, extractor, cache);
        Assert.AreEqual(100, first.TotalTokens);

        File.AppendAllBytes(file, new byte[16]);
        extractor.Outcomes[file].Usages.Clear();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m2", 7));
        var second = Collect(workspace.Path, extractor, cache);

        Assert.HasCount(2, extractor.Batches);
        Assert.AreEqual(7, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_DeletedFile_ContributionDisappears()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DeletedFile_ContributionDisappears));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var kept = WriteSessionFile(workspace.Path, "proj-a", "kept");
        WriteSessionFile(workspace.Path, "proj-a", "deleted");
        var extractor = new StubExtractor();
        extractor.Outcomes[kept] = new FileOutcome();
        extractor.Outcomes[kept].Usages.Add(Usage(BaseTime, "m1", 10));
        var deleted = Path.Combine(workspace.Path, "sessions", "proj-a", "deleted", "session.jsonl.zstd");
        extractor.Outcomes[deleted] = new FileOutcome();
        extractor.Outcomes[deleted].Usages.Add(Usage(BaseTime, "m2", 900));
        var first = Collect(workspace.Path, extractor, cache);
        Assert.AreEqual(910, first.TotalTokens);

        Directory.Delete(Path.GetDirectoryName(deleted)!, recursive: true);
        var second = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(10, second.TotalTokens);
        Assert.AreEqual(1, second.Status.ExpectedSessions);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_TimezoneChange_RebuildsCacheAndBuckets()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_TimezoneChange_RebuildsCacheAndBuckets));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        // 23:30 UTC lands on the next local day in a +13 zone.
        var time = new DateTimeOffset(2026, 7, 19, 23, 30, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var file = WriteSessionFile(workspace.Path, "proj-a", "zoned");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(time, "m1", 5));
        var scanLog = new List<(string Path, DshSessionScanMode Mode)>();
        var plus13 = TimeZoneInfo.CreateCustomTimeZone(
            "plus13", TimeSpan.FromHours(13), "plus13", "plus13");

        var utcResult = Collect(workspace.Path, extractor, cache, scanLog: scanLog);
        var zonedResult = Collect(workspace.Path, extractor, cache, zone: plus13, scanLog: scanLog);

        Assert.AreEqual(DshSessionScanMode.Extract, scanLog[0].Mode);
        Assert.AreEqual(DshSessionScanMode.Extract, scanLog[1].Mode);
        Assert.AreEqual(TimeSpan.Zero, utcResult.Records[0].Timestamp.Offset);
        Assert.AreEqual(TimeSpan.FromHours(13), zonedResult.Records[0].Timestamp.Offset);
        Assert.AreEqual(5, zonedResult.TotalTokens);
    }

    [TestMethod]
    public void Collect_ExtractorProtocolChange_RebuildsCache()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExtractorProtocolChange_RebuildsCache));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "versioned");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 10));
        var scanLog = new List<(string Path, DshSessionScanMode Mode)>();
        Collect(workspace.Path, extractor, cache, scanLog: scanLog);

        extractor.ProtocolVersion = 4;
        var second = Collect(workspace.Path, extractor, cache, scanLog: scanLog);

        Assert.AreEqual(DshSessionScanMode.Extract, scanLog[0].Mode);
        Assert.AreEqual(DshSessionScanMode.Extract, scanLog[1].Mode);
        Assert.AreEqual(10, second.TotalTokens);
        var entry = cache.Get(AgentUsageCacheEntryId.ForPath(file));
        Assert.AreEqual(DshLocalUsageContributor.ParserVersion + "+extractor/4", entry!.ParserVersion);
    }

    [TestMethod]
    public void Collect_CacheEntry_StoresAttributionRecordsNotDays()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CacheEntry_StoresAttributionRecordsNotDays));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "attributed");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 1, 2, 3, 4));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, null, 7));

        Collect(workspace.Path, extractor, cache);

        var entry = cache.Get(AgentUsageCacheEntryId.ForPath(file));
        Assert.IsNotNull(entry);
        // The dedupable record rides under its SHA-256 hash key; the
        // non-dedupable event is a residual day total. No day-keyed map
        // remains on the entry.
        var record = entry!.Records.Single();
        Assert.AreEqual(64, record.Key.Length);
        Assert.AreEqual("2026-07-20", record.Value.Day);
        Assert.AreEqual(10, record.Value.Tokens);
        Assert.AreEqual(7, entry.ResidualDays["2026-07-20"]);
    }

    [TestMethod]
    public void Collect_CacheFiles_NeverContainPathsOrSessionIds()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CacheFiles_NeverContainPathsOrSessionIds));
        var cacheRoot = Path.Combine(workspace.Path, "cache");
        var cache = new FileAgentUsageCacheStore(cacheRoot);
        const string sessionId = "session-secret-777";
        var file = WriteSessionFile(workspace.Path, "work-secret", sessionId);
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1-secret", 1, 2, 3, 4));

        Collect(workspace.Path, extractor, cache);

        var cacheFiles = Directory.GetFiles(cacheRoot, "*.json");
        Assert.HasCount(1, cacheFiles);
        foreach (var cacheFile in cacheFiles)
        {
            var json = File.ReadAllText(cacheFile);
            Assert.DoesNotContain(sessionId, json);
            Assert.DoesNotContain("work-secret", json);
            Assert.DoesNotContain("m1-secret", json);
            Assert.DoesNotContain(workspace.Path, json);
            Assert.DoesNotContain(sessionId, Path.GetFileName(cacheFile));
        }
    }

    [TestMethod]
    public void Collect_LargeScan_BatchesFilesAtTheCap()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_LargeScan_BatchesFilesAtTheCap));
        var extractor = new StubExtractor();
        for (var i = 0; i < DshLocalUsageContributor.MaxBatchFiles + 1; i++)
        {
            var file = WriteSessionFile(workspace.Path, "proj-a", $"session-{i:000}");
            extractor.Outcomes[file] = new FileOutcome();
            extractor.Outcomes[file].Usages.Add(Usage(BaseTime, $"m-{i}", 1));
        }

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(2, extractor.Batches);
        Assert.HasCount(DshLocalUsageContributor.MaxBatchFiles, extractor.Batches[0]);
        Assert.HasCount(1, extractor.Batches[1]);
        Assert.AreEqual(DshLocalUsageContributor.MaxBatchFiles + 1, result.Status.MatchedSessions);
        Assert.AreEqual(DshLocalUsageContributor.MaxBatchFiles + 1, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_BatchProcessFailure_RemainingFilesContributeNothing()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_BatchProcessFailure_RemainingFilesContributeNothing));
        WriteSessionFile(workspace.Path, "proj-a", "session-a");
        // The probe answers, then the real batch dies (non-zero exit).
        var probeOnly = new StubExtractor { FailRealBatches = true };

        var result = Collect(workspace.Path, probeOnly);

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.ExtractorUnavailable);
    }

    [TestMethod]
    public void Collect_SameMessageIdTwoAttempts_BothAttemptsCounted()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SameMessageIdTwoAttempts_BothAttemptsCounted));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        // Retry attempts of one message id are separate fold groups.
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 100, attempt: 1));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, "m1", 40, attempt: 2));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(140, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_ExplicitAndSyntheticSameAttemptNumber_DoNotCollide()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExplicitAndSyntheticSameAttemptNumber_DoNotCollide));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 100, attempt: 1));
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime + 1000, "m1", 40, attempt: 1, attemptSynthetic: true));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(140, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_SameMessageIdChunkThenFinalSameAttempt_CountsOnlyTheFinal()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SameMessageIdChunkThenFinalSameAttempt_CountsOnlyTheFinal));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime, "m1", 100, kind: "assistant/chunk", attempt: 1, seq: 3));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, "m1", 40, attempt: 1));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(40, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_SharedMessageIdAndAttemptAcrossFiles_CountsOnce()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SharedMessageIdAndAttemptAcrossFiles_CountsOnce));
        var fileA = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var fileB = WriteSessionFile(workspace.Path, "proj-a", "session-b");
        var extractor = new StubExtractor();
        extractor.Outcomes[fileA] = new FileOutcome();
        extractor.Outcomes[fileA].Usages.Add(Usage(BaseTime, "shared", 100, attempt: 1));
        extractor.Outcomes[fileB] = new FileOutcome();
        extractor.Outcomes[fileB].Usages.Add(Usage(BaseTime, "shared", 100, attempt: 1));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(100, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_BatchFailureAfterDispatch_DiscardsTheWholeStagedBatch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_BatchFailureAfterDispatch_DiscardsTheWholeStagedBatch));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var fileA = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var fileB = WriteSessionFile(workspace.Path, "proj-a", "session-b");
        var extractor = new StubExtractor { FailAfterDispatch = true };
        extractor.Outcomes[fileA] = new FileOutcome();
        extractor.Outcomes[fileA].Usages.Add(Usage(BaseTime, "m1", 100));
        extractor.Outcomes[fileB] = new FileOutcome();
        extractor.Outcomes[fileB].Usages.Add(Usage(BaseTime, "m2", 11));

        // Valid events streamed for both files, then the batch reported a
        // protocol violation: the rollback journal undoes every fold, cache
        // write and counter the batch applied.
        var failed = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, failed.Status.Status);
        Assert.AreEqual(0, failed.TotalTokens);
        Assert.AreEqual(0, failed.Status.MatchedSessions);
        Assert.AreEqual(0, failed.Status.SkippedFiles);
        Assert.AreEqual(0, failed.Status.BadLines);
        CollectionAssert.Contains(
            failed.Status.Reasons.ToArray(),
            AgentUsageGapReason.ExtractorUnavailable);
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(fileA)));
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(fileB)));

        // The same stub succeeding commits: totals and cache are populated.
        extractor.FailAfterDispatch = false;
        var succeeded = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(AgentUsageSourceStatus.Available, succeeded.Status.Status);
        Assert.AreEqual(111, succeeded.TotalTokens);
        Assert.AreEqual(2, succeeded.Status.MatchedSessions);
        Assert.IsNotNull(cache.Get(AgentUsageCacheEntryId.ForPath(fileA)));
        Assert.IsNotNull(cache.Get(AgentUsageCacheEntryId.ForPath(fileB)));
    }

    [TestMethod]
    public void Collect_BatchFailureAfterDispatch_DiscardsUnpublishedCacheEntries()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_BatchFailureAfterDispatch_DiscardsUnpublishedCacheEntries));
        var cache = new RecordingCacheStore();
        var fileA = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var fileB = WriteSessionFile(workspace.Path, "proj-a", "session-b");
        var extractor = new StubExtractor { FailAfterDispatch = true };
        extractor.Outcomes[fileA] = new FileOutcome();
        extractor.Outcomes[fileA].Usages.Add(Usage(BaseTime, "m1", 100));
        extractor.Outcomes[fileB] = new FileOutcome();
        extractor.Outcomes[fileB].Usages.Add(Usage(BaseTime, "m2", 11));

        Collect(workspace.Path, extractor, cache);

        // Both files reached EOF, so both entries reached the isolated staging
        // namespace; the failed batch publishes neither of them.
        CollectionAssert.AreEqual(
            new[] { AgentUsageCacheEntryId.ForPath(fileA), AgentUsageCacheEntryId.ForPath(fileB) },
            cache.StagedIds.ToArray());
        Assert.IsEmpty(cache.CommittedIds);
        Assert.IsEmpty(cache.SavedIds);
        Assert.IsEmpty(cache.DeletedIds);
    }

    [TestMethod]
    public void Collect_FailedReplacementBatch_PreservesPreviouslyCommittedCache()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_FailedReplacementBatch_PreservesPreviouslyCommittedCache));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 10));
        Collect(workspace.Path, extractor, cache);
        var committed = cache.Get(AgentUsageCacheEntryId.ForPath(file));
        Assert.IsNotNull(committed);

        File.WriteAllBytes(file, Enumerable.Repeat((byte)2, 32).ToArray());
        extractor.Outcomes[file].Usages.Clear();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 99));
        extractor.FailAfterDispatch = true;

        var failed = Collect(workspace.Path, extractor, cache);
        var afterFailure = cache.Get(AgentUsageCacheEntryId.ForPath(file));

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, failed.Status.Status);
        Assert.IsNotNull(afterFailure);
        Assert.AreEqual(committed!.FileSize, afterFailure!.FileSize);
        Assert.AreEqual(10, afterFailure.Records.Single().Value.Tokens);
    }

    [TestMethod]
    public void Collect_StrayBytes_CountOneBadLineAndStayUncached()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_StrayBytes_CountOneBadLineAndStayUncached));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "stray");
        var extractor = new StubExtractor();
        // Garbage bytes outside any frame: the parsed events still count but
        // the file is partial — one bad line, not matched, not cached.
        extractor.Outcomes[file] = new FileOutcome { Stray = 12 };
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 21));

        var result = Collect(workspace.Path, extractor, cache);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.BadLines);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(21, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(file)));
    }

    [TestMethod]
    public void Collect_CleanFileAboveCacheRecordCap_IsMatchedCompleteAndUncached()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CleanFileAboveCacheRecordCap_IsMatchedCompleteAndUncached));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "large");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 10));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, "m2", 20));
        var contributor = new DshLocalUsageContributor(() => workspace.Path, extractor, cache, Utc)
        {
            MaxCachedRecords = 1
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        // A clean parse past the record cap is matched and complete; only the
        // cache write is skipped, so the file re-parses every scan.
        Assert.AreEqual(AgentUsageSourceStatus.Available, status.Status);
        Assert.AreEqual(1, status.ExpectedSessions);
        Assert.AreEqual(1, status.MatchedSessions);
        Assert.IsEmpty(status.Reasons);
        Assert.AreEqual(30, sink.Records.Sum(record => record.InputTokens));
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(file)));
    }

    [TestMethod]
    public void Collect_FoldGroupCapExceeded_SpillStillAppliesFinalSampleWins()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_FoldGroupCapExceeded_SpillStillAppliesFinalSampleWins));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var file = WriteSessionFile(workspace.Path, "proj-a", "many-groups");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 10));
        extractor.Outcomes[file].Usages.Add(Usage(
            BaseTime + 500, "m2", 100, kind: "assistant/chunk", seq: 1));
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime + 1000, "m2", 20));
        var contributor = new DshLocalUsageContributor(
            () => workspace.Path,
            extractor,
            cache,
            Utc,
            () => Path.Combine(workspace.Path, ".fold-spill"))
        {
            MaxFoldGroups = 1
        };
        var sink = new RecordingSink();

        var firstStatus = contributor.Collect(sink, CancellationToken.None);
        var secondSink = new RecordingSink();
        var secondStatus = contributor.Collect(secondSink, CancellationToken.None);

        // m1 opens the only in-memory group. Both m2 samples spill, and the
        // final 20 replaces the chunk 100 exactly as it would in memory.
        Assert.AreEqual(AgentUsageSourceStatus.Available, firstStatus.Status);
        Assert.AreEqual(AgentUsageSourceStatus.Available, secondStatus.Status);
        Assert.IsEmpty(secondStatus.Reasons);
        Assert.AreEqual(1, secondStatus.MatchedSessions);
        Assert.AreEqual(30, sink.Records.Sum(record => record.InputTokens));
        Assert.AreEqual(30, secondSink.Records.Sum(record => record.InputTokens));
        Assert.AreEqual(0, secondStatus.BadLines);
        Assert.IsFalse(Directory.EnumerateDirectories(
            Path.Combine(workspace.Path, ".fold-spill"), ".fold-*").Any());
    }

    [TestMethod]
    public void Collect_ExactlyAtFileDiscoveryCap_ReportsNoTruncation()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExactlyAtFileDiscoveryCap_ReportsNoTruncation));
        var extractor = new StubExtractor();
        for (var i = 0; i < 2; i++)
        {
            var file = WriteSessionFile(workspace.Path, "proj-a", $"session-{i}");
            extractor.Outcomes[file] = new FileOutcome();
            extractor.Outcomes[file].Usages.Add(Usage(BaseTime, $"m{i}", 1));
        }

        var contributor = new DshLocalUsageContributor(() => workspace.Path, extractor, null, Utc)
        {
            MaxDiscoveredFiles = 2
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        // Reaching the cap exactly at a clean boundary (no (cap+1)-th file)
        // is complete discovery, not truncation.
        Assert.AreEqual(AgentUsageSourceStatus.Available, status.Status);
        Assert.AreEqual(2, status.ExpectedSessions);
        Assert.AreEqual(2, status.MatchedSessions);
        Assert.IsEmpty(status.Reasons);
    }

    [TestMethod]
    public void Collect_ExactlyAtDirectoryDiscoveryCap_ReportsNoTruncation()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExactlyAtDirectoryDiscoveryCap_ReportsNoTruncation));
        var file = WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var extractor = new StubExtractor();
        extractor.Outcomes[file] = new FileOutcome();
        extractor.Outcomes[file].Usages.Add(Usage(BaseTime, "m1", 5));

        var contributor = new DshLocalUsageContributor(
            () => workspace.Path, extractor, null, Utc)
        {
            // sessions root + proj-a + session-a: exactly three visits.
            MaxDiscoveredDirectories = 3
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        Assert.AreEqual(AgentUsageSourceStatus.Available, status.Status);
        Assert.AreEqual(1, status.ExpectedSessions);
        Assert.AreEqual(1, status.MatchedSessions);
        Assert.IsEmpty(status.Reasons);
    }

    [TestMethod]
    public void Collect_DiscoveryFileCapReached_ReportsDiscoveryTruncated()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DiscoveryFileCapReached_ReportsDiscoveryTruncated));
        var extractor = new StubExtractor();
        for (var i = 0; i < 3; i++)
        {
            var file = WriteSessionFile(workspace.Path, "proj-a", $"session-{i}");
            extractor.Outcomes[file] = new FileOutcome();
            extractor.Outcomes[file].Usages.Add(Usage(BaseTime, $"m{i}", 1));
        }

        var contributor = new DshLocalUsageContributor(() => workspace.Path, extractor, null, Utc)
        {
            MaxDiscoveredFiles = 2
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, status.Status);
        Assert.AreEqual(2, status.ExpectedSessions);
        Assert.AreEqual(2, status.MatchedSessions);
        Assert.AreEqual(2, sink.Records.Sum(record => record.InputTokens));
        CollectionAssert.Contains(
            status.Reasons.ToArray(),
            AgentUsageGapReason.DiscoveryTruncated);
    }

    [TestMethod]
    public void Collect_DiscoveryDirectoryCapReached_ReportsDiscoveryTruncated()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DiscoveryDirectoryCapReached_ReportsDiscoveryTruncated));
        WriteSessionFile(workspace.Path, "proj-a", "session-a");
        var contributor = new DshLocalUsageContributor(
            () => workspace.Path, new StubExtractor(), null, Utc)
        {
            // Only the sessions root itself is visited.
            MaxDiscoveredDirectories = 1
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, status.Status);
        Assert.AreEqual(0, status.ExpectedSessions);
        Assert.IsEmpty(sink.Records);
        CollectionAssert.Contains(
            status.Reasons.ToArray(),
            AgentUsageGapReason.DiscoveryTruncated);
    }

    [TestMethod]
    public void ShouldDescend_NormalDirectory_ReturnsTrue()
    {
        using var workspace = TestWorkspace.Create(nameof(ShouldDescend_NormalDirectory_ReturnsTrue));
        var directory = Directory.CreateDirectory(Path.Combine(workspace.Path, "plain"));

        Assert.IsTrue(UsageDiscoveryGuard.ShouldDescend(directory));
    }

    [TestMethod]
    public void ShouldDescend_Junction_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(ShouldDescend_Junction_ReturnsFalse));
        var target = Directory.CreateDirectory(Path.Combine(workspace.Path, "target"));
        var link = Path.Combine(workspace.Path, "link");
        if (!TryCreateJunction(link, target.FullName))
            Assert.Inconclusive("Junction creation is not permitted on this host.");

        Assert.IsFalse(UsageDiscoveryGuard.ShouldDescend(new DirectoryInfo(link)));
    }

    [TestMethod]
    public void Collect_JunctionedSessionTree_IsNotDescended()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_JunctionedSessionTree_IsNotDescended));
        var real = WriteSessionFile(workspace.Path, "proj-a", "real");
        var outsideTree = Path.Combine(workspace.Path, "outside-tree");
        var linkedSession = Path.Combine(outsideTree, "linked", "session.jsonl.zstd");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedSession)!);
        File.WriteAllBytes(linkedSession, new byte[16]);
        if (!TryCreateJunction(
                Path.Combine(workspace.Path, "sessions", "proj-linked"),
                outsideTree))
        {
            Assert.Inconclusive("Junction creation is not permitted on this host.");
        }

        var extractor = new StubExtractor();
        extractor.Outcomes[real] = new FileOutcome();
        extractor.Outcomes[real].Usages.Add(Usage(BaseTime, "m1", 7));

        var result = Collect(workspace.Path, extractor);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(7, result.TotalTokens);
        Assert.HasCount(1, extractor.Batches);
        CollectionAssert.AreEqual(new[] { real }, extractor.Batches[0]);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(link);
            startInfo.ArgumentList.Add(target);
            using var process = Process.Start(startInfo);
            if (process == null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
