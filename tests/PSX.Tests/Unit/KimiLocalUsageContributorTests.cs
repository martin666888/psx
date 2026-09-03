using System.Diagnostics;
using System.Text;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiLocalUsageContributorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // 2026-07-20T04:40:00Z, matching the persisted 0.29.1 fixture shape.
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

    private sealed class ScanLog
    {
        public List<(string Path, KimiWireScanMode Mode, long Bytes)> Entries { get; } = [];

        public (string Path, KimiWireScanMode Mode, long Bytes) For(string wirePath, int index) =>
            Entries.Where(entry => entry.Path == wirePath).ElementAt(index);
    }

    private static string Metadata(string version = "1.1") =>
        $$"""{"type":"metadata","protocol_version":"{{version}}","created_at":1}""";

    private static string Usage(
        long time,
        long input,
        long output,
        long cacheRead,
        long cacheCreation) =>
        "{\"type\":\"usage.record\",\"time\":" + time
        + ",\"model\":\"kimi-test\",\"usage\":{\"inputOther\":" + input
        + ",\"output\":" + output
        + ",\"inputCacheRead\":" + cacheRead
        + ",\"inputCacheCreation\":" + cacheCreation + "}}";

    private static string WirePath(
        string home,
        string workDirKey,
        string sessionId,
        string agentId) =>
        Path.Combine(home, "sessions", workDirKey, sessionId, "agents", agentId, "wire.jsonl");

    private static string WriteWire(
        string home,
        string workDirKey,
        string sessionId,
        string agentId,
        params string[] lines)
    {
        var path = WirePath(home, workDirKey, sessionId, agentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private static void WriteState(
        string home,
        string workDirKey,
        string sessionId,
        string stateJson)
    {
        var directory = Path.Combine(home, "sessions", workDirKey, sessionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "state.json"), stateJson);
    }

    private static CollectResult Collect(
        string home,
        IAgentUsageCacheStore? cache = null,
        TimeZoneInfo? zone = null,
        ScanLog? log = null)
    {
        var contributor = new KimiLocalUsageContributor(
            () => home, cache, zone ?? Utc);
        if (log != null)
            contributor.ScanObserver = (path, mode, bytes) => log.Entries.Add((path, mode, bytes));
        var sink = new RecordingSink();
        var status = contributor.Collect(sink, CancellationToken.None);
        return new CollectResult(status, sink.Records);
    }

    [TestMethod]
    public void Descriptor_ClaimsTheAcpKimiProvider()
    {
        var contributor = new KimiLocalUsageContributor(() => null);

        Assert.AreEqual("acp-kimi", contributor.Descriptor.Key);
        Assert.AreEqual("Kimi Code", contributor.Descriptor.DisplayName);
        Assert.AreEqual("kimi", contributor.Descriptor.IconKey);
        Assert.AreEqual("acp-kimi", contributor.Descriptor.TrackedProviderKey);
    }

    [TestMethod]
    public void Collect_MissingSessionsDirectory_IsAvailableZero()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingSessionsDirectory_IsAvailableZero));

        var result = Collect(Path.Combine(workspace.Path, "no-such-home"));

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(0, result.Status.ExpectedSessions);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(0, result.Status.ScannedFiles);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_EmptySessionsDirectory_IsAvailableZero()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_EmptySessionsDirectory_IsAvailableZero));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "sessions"));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(0, result.Status.ExpectedSessions);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_Real0291Fixture_RecognizesPersistedWireShape()
    {
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "KimiUsage",
            "0.29.1");

        var result = Collect(fixtureRoot);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(110, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_SessionOutsideAnyThreadStore_IsCounted()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SessionOutsideAnyThreadStore_IsCounted));
        WriteWire(workspace.Path, "work-a", "cli-session", "main",
            Metadata(), Usage(BaseTime, 10, 20, 30, 40));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(100, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_MainAndSubagentUsage_AreSummedPerDay()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MainAndSubagentUsage_AreSummedPerDay));
        var secondDay = new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        WriteWire(workspace.Path, "work-a", "session-a", "main",
            Metadata(), Usage(BaseTime, 10, 20, 30, 40));
        WriteWire(workspace.Path, "work-a", "session-a", "sub-1",
            Metadata(), Usage(BaseTime + 1000, 1, 2, 3, 4), Usage(secondDay, 5, 5, 5, 5));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(130, result.TotalTokens);
        // Records fold globally per day at the end of the scan, so the two
        // wires of day one merge into a single 110-token record.
        Assert.HasCount(2, result.Records);
        Assert.IsTrue(result.Records.Any(record => record.InputTokens == 110));
        Assert.IsTrue(result.Records.Any(record => record.InputTokens == 20));
    }

    [TestMethod]
    public void Collect_RecognizedSetupOnlySession_IsExactZeroAndMatched()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_RecognizedSetupOnlySession_IsExactZeroAndMatched));
        WriteWire(workspace.Path, "work-a", "empty-session", "main",
            Metadata(),
            """{"type":"config.update","time":1}""");

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_ActivityWithoutMetadataOrUsage_IsUnsupportedFormat()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ActivityWithoutMetadataOrUsage_IsUnsupportedFormat));
        WriteWire(workspace.Path, "work-a", "active-session", "main",
            """{"type":"turn.prompt","time":2,"input":[]}""");

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_UnknownProtocolWithKnownUsageShape_RemainsAvailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnknownProtocolWithKnownUsageShape_RemainsAvailable));
        WriteWire(workspace.Path, "work-a", "future-session", "main",
            Metadata("9.0"),
            Usage(BaseTime, 1, 2, 3, 4));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(10, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_SameSessionFork_IsNotDegraded()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SameSessionFork_IsNotDegraded));
        WriteState(workspace.Path, "work-a", "fork-session",
            """{"agents":{"main":{"type":"main"},"agent-18":{"type":"sub","forkedFrom":"main"}}}""");
        WriteWire(workspace.Path, "work-a", "fork-session", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));
        WriteWire(workspace.Path, "work-a", "fork-session", "agent-18",
            Metadata(), Usage(BaseTime + 60_000, 5, 6, 7, 8));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(36, result.TotalTokens);
        Assert.IsEmpty(result.Status.Reasons);
    }

    [TestMethod]
    public void Collect_UnrecognizedForkedFrom_IsPartialWithLineageUnresolved()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnrecognizedForkedFrom_IsPartialWithLineageUnresolved));
        WriteState(workspace.Path, "work-a", "orphan-fork",
            """{"agents":{"main":{"type":"main"},"agent-18":{"type":"sub","forkedFrom":"other-session/main"}}}""");
        WriteWire(workspace.Path, "work-a", "orphan-fork", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(10, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_IdenticalRecordAcrossSessions_IsCountedOnceAndFlagged()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_IdenticalRecordAcrossSessions_IsCountedOnceAndFlagged));
        var shared = Usage(BaseTime, 10, 20, 30, 40);
        WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), shared);
        WriteWire(workspace.Path, "work-b", "session-b", "main", Metadata(), shared);

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(2, result.Status.MatchedSessions);
        Assert.AreEqual(100, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_CorruptStateJson_SkipsLineageButCountsNormally()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptStateJson_SkipsLineageButCountsNormally));
        WriteState(workspace.Path, "work-a", "broken-state", "{ not-json");
        WriteWire(workspace.Path, "work-a", "broken-state", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.SkippedFiles);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(10, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
        CollectionAssert.DoesNotContain(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
    }

    [TestMethod]
    public void Collect_MissingMainWire_SkipsMatchButCountsSubagent()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingMainWire_SkipsMatchButCountsSubagent));
        WriteWire(workspace.Path, "work-a", "no-main", "sub-1",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(10, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.MissingSessionLogs);
    }

    [TestMethod]
    public void Collect_CorruptLine_CountsBadLineAndKeepsParsedRecords()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptLine_CountsBadLineAndKeepsParsedRecords));
        WriteWire(workspace.Path, "work-a", "bad-line", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4), "{ not-json");

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.BadLines);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.AreEqual(10, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_OversizedLine_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OversizedLine_CountsBadLine));
        WriteWire(workspace.Path, "work-a", "oversized", "main",
            Metadata(),
            Usage(BaseTime, 1, 2, 3, 4),
            new string('x', BoundedJsonlReader.MaxLineBytes + 1));

        var result = Collect(workspace.Path);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.BadLines);
        Assert.AreEqual(10, result.TotalTokens);
    }

    [TestMethod]
    public void Collect_UnchangedFile_UsesCacheWithoutReparsing()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnchangedFile_UsesCacheWithoutReparsing));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(workspace.Path, "work-a", "cached", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));
        var log = new ScanLog();

        var first = Collect(workspace.Path, cache, log: log);
        var second = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 0).Mode);
        Assert.AreEqual(KimiWireScanMode.CacheHit, log.For(wire, 1).Mode);
        Assert.AreEqual(0, log.For(wire, 1).Bytes);
        Assert.AreEqual(first.TotalTokens, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_SameFileDuplicate_RemainsPartialAfterCacheHit()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SameFileDuplicate_RemainsPartialAfterCacheHit));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var usage = Usage(BaseTime, 1, 2, 3, 4);
        var wire = WriteWire(
            workspace.Path, "work-a", "duplicate", "main", Metadata(), usage, usage);
        var log = new ScanLog();

        var first = Collect(workspace.Path, cache, log: log);
        var second = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(10, first.TotalTokens);
        Assert.AreEqual(10, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, first.Status.Status);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, second.Status.Status);
        CollectionAssert.Contains(
            second.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(KimiWireScanMode.CacheHit, log.For(wire, 1).Mode);
    }

    [TestMethod]
    public void Collect_AppendedBytes_ParseOnlyTheIncrement()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_AppendedBytes_ParseOnlyTheIncrement));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(workspace.Path, "work-a", "appending", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));
        var log = new ScanLog();
        var first = Collect(workspace.Path, cache, log: log);

        var appended = Usage(BaseTime + 1000, 10, 0, 0, 0) + "\n";
        File.AppendAllText(wire, appended);
        var second = Collect(workspace.Path, cache, log: log);

        var appendScan = log.For(wire, 1);
        Assert.AreEqual(KimiWireScanMode.Append, appendScan.Mode);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(appended), appendScan.Bytes);
        Assert.AreEqual(first.TotalTokens + 10, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_TruncatedFile_RescansFromScratch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_TruncatedFile_RescansFromScratch));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(workspace.Path, "work-a", "truncated", "main",
            Metadata(), Usage(BaseTime, 100, 0, 0, 0), Usage(BaseTime + 1000, 100, 0, 0, 0));
        var log = new ScanLog();
        var first = Collect(workspace.Path, cache, log: log);
        Assert.AreEqual(200, first.TotalTokens);

        File.WriteAllText(wire, Metadata() + "\n" + Usage(BaseTime, 7, 0, 0, 0) + "\n");
        var second = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 1).Mode);
        Assert.AreEqual(7, second.TotalTokens);
    }

    [TestMethod]
    public void Collect_RewrittenSameSizeFile_RescansFromScratch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_RewrittenSameSizeFile_RescansFromScratch));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(workspace.Path, "work-a", "rewritten", "main",
            Metadata(), Usage(BaseTime, 100, 0, 0, 0));
        var log = new ScanLog();
        Collect(workspace.Path, cache, log: log);

        File.WriteAllText(wire, Metadata() + "\n" + Usage(BaseTime, 900, 0, 0, 0) + "\n");
        File.SetLastWriteTimeUtc(wire, DateTime.UtcNow.AddMinutes(1));
        var second = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 1).Mode);
        Assert.AreEqual(900, second.TotalTokens);
    }

    [TestMethod]
    public void Collect_TimezoneChange_RebuildsCacheAndBuckets()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_TimezoneChange_RebuildsCacheAndBuckets));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        // 23:30 UTC lands on the next local day in a +13 zone.
        var time = new DateTimeOffset(2026, 7, 19, 23, 30, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var wire = WriteWire(workspace.Path, "work-a", "zoned", "main",
            Metadata(), Usage(time, 5, 0, 0, 0));
        var log = new ScanLog();
        var plus13 = TimeZoneInfo.CreateCustomTimeZone(
            "plus13", TimeSpan.FromHours(13), "plus13", "plus13");

        var utcResult = Collect(workspace.Path, cache, zone: Utc, log: log);
        var zonedResult = Collect(workspace.Path, cache, zone: plus13, log: log);

        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 0).Mode);
        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 1).Mode);
        Assert.AreEqual(TimeSpan.Zero, utcResult.Records[0].Timestamp.Offset);
        Assert.AreEqual(TimeSpan.FromHours(13), zonedResult.Records[0].Timestamp.Offset);
        Assert.AreEqual(5, zonedResult.TotalTokens);
    }

    [TestMethod]
    public void Collect_ParserVersionMismatch_TreatsCacheAsMiss()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ParserVersionMismatch_TreatsCacheAsMiss));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(workspace.Path, "work-a", "versioned", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));
        var log = new ScanLog();
        Collect(workspace.Path, cache, log: log);

        var entryId = AgentUsageCacheEntryId.ForPath(wire);
        var entry = cache.Get(entryId);
        Assert.IsNotNull(entry);
        cache.Save(entryId, entry! with { ParserVersion = "outdated" });

        var second = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 1).Mode);
        Assert.AreEqual(10, second.TotalTokens);
        Assert.AreEqual(
            KimiLocalUsageContributor.ParserVersion,
            cache.Get(entryId)!.ParserVersion);
    }

    [TestMethod]
    public void Collect_CorruptCacheFile_IsMissAndLogsStillParse()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptCacheFile_IsMissAndLogsStillParse));
        var cacheRoot = Path.Combine(workspace.Path, "cache");
        var cache = new FileAgentUsageCacheStore(cacheRoot);
        var wire = WriteWire(workspace.Path, "work-a", "corrupt-cache", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));
        var log = new ScanLog();
        Collect(workspace.Path, cache, log: log);

        var cacheFile = Directory.GetFiles(cacheRoot, "*.json").Single();
        File.WriteAllText(cacheFile, "{ not-json");
        var second = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(KimiWireScanMode.Full, log.For(wire, 1).Mode);
        Assert.AreEqual(10, second.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_CacheFiles_NeverContainPathsOrSessionIds()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CacheFiles_NeverContainPathsOrSessionIds));
        var cacheRoot = Path.Combine(workspace.Path, "cache");
        var cache = new FileAgentUsageCacheStore(cacheRoot);
        const string sessionId = "session-secret-777";
        WriteWire(workspace.Path, "work-secret", sessionId, "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));

        Collect(workspace.Path, cache);

        var cacheFiles = Directory.GetFiles(cacheRoot, "*.json");
        Assert.HasCount(1, cacheFiles);
        foreach (var cacheFile in cacheFiles)
        {
            var json = File.ReadAllText(cacheFile);
            Assert.DoesNotContain(sessionId, json);
            Assert.DoesNotContain("work-secret", json);
            Assert.DoesNotContain(workspace.Path, json);
            Assert.DoesNotContain(sessionId, Path.GetFileName(cacheFile));
        }
    }

    [TestMethod]
    public void Collect_DeletedFile_ContributionDisappears()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DeletedFile_ContributionDisappears));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        WriteWire(workspace.Path, "work-a", "kept", "main",
            Metadata(), Usage(BaseTime, 10, 0, 0, 0));
        WriteWire(workspace.Path, "work-a", "deleted", "main",
            Metadata(), Usage(BaseTime, 900, 0, 0, 0));
        var first = Collect(workspace.Path, cache);
        Assert.AreEqual(910, first.TotalTokens);

        Directory.Delete(Path.Combine(workspace.Path, "sessions", "work-a", "deleted"), recursive: true);
        var second = Collect(workspace.Path, cache);

        Assert.AreEqual(10, second.TotalTokens);
        Assert.AreEqual(1, second.Status.ExpectedSessions);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_ForkAddedAfterParentCached_IsNotDoubleCounted()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ForkAddedAfterParentCached_IsNotDoubleCounted));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var shared = Usage(BaseTime, 10, 20, 30, 40);
        WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), shared);
        var first = Collect(workspace.Path, cache);
        Assert.AreEqual(100, first.TotalTokens);
        Assert.AreEqual(AgentUsageSourceStatus.Available, first.Status.Status);

        // A fork session appears whose wire carries the identical records.
        WriteWire(workspace.Path, "work-a", "session-b", "main", Metadata(), shared);
        var second = Collect(workspace.Path, cache);

        Assert.AreEqual(100, second.TotalTokens);
        Assert.AreEqual(2, second.Status.MatchedSessions);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, second.Status.Status);
        CollectionAssert.Contains(
            second.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
    }

    [TestMethod]
    public void Collect_HotParentPlusColdChild_AttributesEachRecordOnce()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_HotParentPlusColdChild_AttributesEachRecordOnce));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var shared = Usage(BaseTime, 10, 20, 30, 40);
        var parentWire = WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), shared);
        Collect(workspace.Path, cache);

        // One scan mixing a cache-hit parent with a freshly parsed child.
        WriteWire(workspace.Path, "work-a", "session-b", "main",
            Metadata(), shared, Usage(BaseTime + 1000, 1, 2, 3, 4));
        var log = new ScanLog();
        var result = Collect(workspace.Path, cache, log: log);

        Assert.AreEqual(KimiWireScanMode.CacheHit, log.For(parentWire, 0).Mode);
        Assert.AreEqual(110, result.TotalTokens);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
    }

    [TestMethod]
    public void Collect_DeletedParent_RestoresChildAttribution()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DeletedParent_RestoresChildAttribution));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var shared = Usage(BaseTime, 10, 20, 30, 40);
        WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), shared);
        WriteWire(workspace.Path, "work-a", "session-b", "main", Metadata(), shared);
        var first = Collect(workspace.Path, cache);
        Assert.AreEqual(100, first.TotalTokens);
        Assert.AreEqual(2, first.Status.MatchedSessions);

        // With the parent gone, the child's cached copy attributes again.
        Directory.Delete(
            Path.Combine(workspace.Path, "sessions", "work-a", "session-a"), recursive: true);
        var second = Collect(workspace.Path, cache);

        Assert.AreEqual(100, second.TotalTokens);
        Assert.AreEqual(1, second.Status.MatchedSessions);
        Assert.AreEqual(AgentUsageSourceStatus.Available, second.Status.Status);
    }

    [TestMethod]
    public void Collect_CacheEntry_StoresAttributionRecordsNotDays()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CacheEntry_StoresAttributionRecordsNotDays));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(workspace.Path, "work-a", "attributed", "main",
            Metadata(), Usage(BaseTime, 1, 2, 3, 4));

        Collect(workspace.Path, cache);

        var entry = cache.Get(AgentUsageCacheEntryId.ForPath(wire));
        Assert.IsNotNull(entry);
        // One dedupable record under its SHA-256 line hash; no day-keyed map
        // and no residual days for Kimi wires.
        var record = entry!.Records.Single();
        Assert.AreEqual(64, record.Key.Length);
        Assert.AreEqual("2026-07-20", record.Value.Day);
        Assert.AreEqual(10, record.Value.Tokens);
        Assert.IsEmpty(entry.ResidualDays);
    }

    [TestMethod]
    public void Collect_DiscoverySessionCapReached_ReportsDiscoveryTruncated()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DiscoverySessionCapReached_ReportsDiscoveryTruncated));
        WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), Usage(BaseTime, 1, 0, 0, 0));
        WriteWire(workspace.Path, "work-a", "session-b", "main", Metadata(), Usage(BaseTime, 2, 0, 0, 0));
        var contributor = new KimiLocalUsageContributor(() => workspace.Path, null, Utc)
        {
            MaxDiscoveredSessions = 1
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, status.Status);
        Assert.AreEqual(1, status.ExpectedSessions);
        Assert.AreEqual(1, status.MatchedSessions);
        CollectionAssert.Contains(
            status.Reasons.ToArray(),
            AgentUsageGapReason.DiscoveryTruncated);
    }

    [TestMethod]
    public void Collect_ExactlyAtSessionDiscoveryCap_ReportsNoTruncation()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExactlyAtSessionDiscoveryCap_ReportsNoTruncation));
        WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), Usage(BaseTime, 1, 0, 0, 0));
        WriteWire(workspace.Path, "work-a", "session-b", "main", Metadata(), Usage(BaseTime + 1000, 2, 0, 0, 0));
        var contributor = new KimiLocalUsageContributor(() => workspace.Path, null, Utc)
        {
            MaxDiscoveredSessions = 2
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        // Reaching the cap exactly at a clean boundary is complete discovery;
        // only a (cap+1)-th session flags truncation.
        Assert.AreEqual(AgentUsageSourceStatus.Available, status.Status);
        Assert.AreEqual(2, status.ExpectedSessions);
        Assert.AreEqual(2, status.MatchedSessions);
        Assert.IsEmpty(status.Reasons);
    }

    [TestMethod]
    public void Collect_SessionDiscoveryCapPlusOne_ReportsDiscoveryTruncated()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SessionDiscoveryCapPlusOne_ReportsDiscoveryTruncated));
        WriteWire(workspace.Path, "work-a", "session-a", "main", Metadata(), Usage(BaseTime, 1, 0, 0, 0));
        WriteWire(workspace.Path, "work-a", "session-b", "main", Metadata(), Usage(BaseTime + 1000, 2, 0, 0, 0));
        WriteWire(workspace.Path, "work-a", "session-c", "main", Metadata(), Usage(BaseTime + 2000, 4, 0, 0, 0));
        var contributor = new KimiLocalUsageContributor(() => workspace.Path, null, Utc)
        {
            MaxDiscoveredSessions = 2
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, status.Status);
        Assert.AreEqual(2, status.ExpectedSessions);
        Assert.AreEqual(2, status.MatchedSessions);
        Assert.AreEqual(3, sink.Records.Sum(record => record.InputTokens));
        CollectionAssert.Contains(
            status.Reasons.ToArray(),
            AgentUsageGapReason.DiscoveryTruncated);
    }

    [TestMethod]
    public void Collect_RecordsPastCacheCap_StillAttributeGloballyInSameScan()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_RecordsPastCacheCap_StillAttributeGloballyInSameScan));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var shared = Usage(BaseTime + 1000, 20, 0, 0, 0);
        // session-a exceeds the per-file entry cap, so its second record is
        // never written to the entry map — but it still merges into the
        // global attribution index as it parses.
        WriteWire(workspace.Path, "work-a", "session-a", "main",
            Metadata(), Usage(BaseTime, 10, 0, 0, 0), shared);
        // session-b re-delivers that record plus one of its own.
        WriteWire(workspace.Path, "work-a", "session-b", "main",
            Metadata(), shared, Usage(BaseTime + 2000, 5, 0, 0, 0));
        var contributor = new KimiLocalUsageContributor(() => workspace.Path, cache, Utc)
        {
            MaxCachedRecords = 1
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        // Global first-wins dedup keys on the parsed records, not on the
        // truncated entry maps: the shared record counts once and the
        // duplicate flags unresolved lineage.
        Assert.AreEqual(AgentUsageSourceStatus.Partial, status.Status);
        CollectionAssert.Contains(
            status.Reasons.ToArray(),
            AgentUsageGapReason.LineageUnresolved);
        Assert.AreEqual(2, status.MatchedSessions);
        Assert.AreEqual(35, sink.Records.Sum(record => record.InputTokens));
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(
            WirePath(workspace.Path, "work-a", "session-a", "main"))));
    }

    [TestMethod]
    public void Collect_CleanWireAboveCacheRecordCap_IsMatchedCompleteAndUncached()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CleanWireAboveCacheRecordCap_IsMatchedCompleteAndUncached));
        var cache = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        var wire = WriteWire(
            workspace.Path, "work-a", "session-a", "main",
            Metadata(),
            Usage(BaseTime, 10, 0, 0, 0),
            Usage(BaseTime + 1000, 20, 0, 0, 0));
        var contributor = new KimiLocalUsageContributor(() => workspace.Path, cache, Utc)
        {
            MaxCachedRecords = 1
        };
        var sink = new RecordingSink();

        var status = contributor.Collect(sink, CancellationToken.None);

        // Matched is a property of the clean parse, never of cacheability.
        Assert.AreEqual(AgentUsageSourceStatus.Available, status.Status);
        Assert.AreEqual(1, status.ExpectedSessions);
        Assert.AreEqual(1, status.MatchedSessions);
        Assert.IsEmpty(status.Reasons);
        Assert.AreEqual(30, sink.Records.Sum(record => record.InputTokens));
        Assert.IsNull(cache.Get(AgentUsageCacheEntryId.ForPath(wire)));
    }

    [TestMethod]
    public void Collect_JunctionedWorkDirectory_IsNotDescended()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_JunctionedWorkDirectory_IsNotDescended));
        WriteWire(
            workspace.Path, "work-a", "session-a", "main", Metadata(), Usage(BaseTime, 7, 0, 0, 0));
        var outsideTree = Path.Combine(workspace.Path, "outside-tree");
        var linkedWire = Path.Combine(
            outsideTree, "session-b", "agents", "main", "wire.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedWire)!);
        File.WriteAllText(linkedWire, Metadata() + "\n" + Usage(BaseTime, 99, 0, 0, 0) + "\n");
        if (!TryCreateJunction(
                Path.Combine(workspace.Path, "sessions", "work-linked"),
                outsideTree))
        {
            Assert.Inconclusive("Junction creation is not permitted on this host.");
        }

        var contributor = new KimiLocalUsageContributor(() => workspace.Path, null, Utc);
        var sink = new RecordingSink();
        var status = contributor.Collect(sink, CancellationToken.None);

        Assert.AreEqual(AgentUsageSourceStatus.Available, status.Status);
        Assert.AreEqual(1, status.ExpectedSessions);
        Assert.AreEqual(1, status.MatchedSessions);
        Assert.AreEqual(7, sink.Records.Sum(record => record.InputTokens));
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

[TestClass]
[TestCategory("Unit")]
public sealed class FileAgentUsageCacheStoreTests
{
    private static AgentUsageCacheEntry Entry(long size = 10, long consumed = 10) =>
        new(
            "parser/v1",
            "UTC",
            size,
            123456789,
            consumed,
            0,
            "abcd",
            true,
            new Dictionary<string, AgentUsageCacheRecord>
            {
                ["0123456789abcdef"] = new AgentUsageCacheRecord("2026-07-20", 42)
            },
            new Dictionary<string, long> { ["2026-07-21"] = 7 });

    [TestMethod]
    public void SaveThenGet_RoundTripsEntry()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveThenGet_RoundTripsEntry));
        var store = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));

        store.Save("entry-1", Entry());
        var loaded = store.Get("entry-1");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("parser/v1", loaded!.ParserVersion);
        Assert.AreEqual("UTC", loaded.TimezoneId);
        Assert.AreEqual(10, loaded.FileSize);
        Assert.AreEqual(123456789, loaded.MtimeUtc);
        Assert.AreEqual(10, loaded.ConsumedLength);
        Assert.AreEqual(0, loaded.TailOffset);
        Assert.AreEqual("abcd", loaded.TailHash);
        Assert.IsTrue(loaded.Recognized);
        var record = loaded.Records.Single();
        Assert.AreEqual("0123456789abcdef", record.Key);
        Assert.AreEqual("2026-07-20", record.Value.Day);
        Assert.AreEqual(42, record.Value.Tokens);
        Assert.AreEqual(7, loaded.ResidualDays["2026-07-21"]);
    }

    [TestMethod]
    public void SaveTwice_ReplacesExistingEntry()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveTwice_ReplacesExistingEntry));
        var store = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));

        store.Save("entry-1", Entry());
        store.Save("entry-1", Entry(size: 20, consumed: 20));

        Assert.AreEqual(20, store.Get("entry-1")!.FileSize);
    }

    [TestMethod]
    public void BatchSave_IsInvisibleUntilCommitAndDisposeDiscardsIt()
    {
        using var workspace = TestWorkspace.Create(nameof(BatchSave_IsInvisibleUntilCommitAndDisposeDiscardsIt));
        var store = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));
        store.Save("entry-1", Entry());

        using (var abandoned = store.BeginBatch())
        {
            abandoned.Save("entry-1", Entry(size: 20, consumed: 20));
            Assert.AreEqual(10, store.Get("entry-1")!.FileSize);
        }
        Assert.AreEqual(10, store.Get("entry-1")!.FileSize);

        using (var committed = store.BeginBatch())
        {
            committed.Save("entry-1", Entry(size: 30, consumed: 30));
            Assert.AreEqual(10, store.Get("entry-1")!.FileSize);
            committed.Commit();
        }
        Assert.AreEqual(30, store.Get("entry-1")!.FileSize);
    }

    [TestMethod]
    public void Get_MissingEntryOrRoot_ReturnsNull()
    {
        using var workspace = TestWorkspace.Create(nameof(Get_MissingEntryOrRoot_ReturnsNull));
        var store = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));

        Assert.IsNull(store.Get("never-written"));

        store.Save("entry-1", Entry());
        Assert.IsNull(store.Get("other"));
    }

    [TestMethod]
    public void Get_CorruptOrMalformedFile_ReturnsNull()
    {
        using var workspace = TestWorkspace.Create(nameof(Get_CorruptOrMalformedFile_ReturnsNull));
        var root = Path.Combine(workspace.Path, "cache");
        var store = new FileAgentUsageCacheStore(root);
        store.Save("entry-1", Entry());

        File.WriteAllText(Path.Combine(root, "entry-1.json"), "{ not-json");
        Assert.IsNull(store.Get("entry-1"));

        File.WriteAllText(
            Path.Combine(root, "entry-1.json"),
            """{"parserVersion":"","timezoneId":"UTC","fileSize":-1}""");
        Assert.IsNull(store.Get("entry-1"));
    }

    [TestMethod]
    public void Delete_RemovesEntryAndNeverThrows()
    {
        using var workspace = TestWorkspace.Create(nameof(Delete_RemovesEntryAndNeverThrows));
        var store = new FileAgentUsageCacheStore(Path.Combine(workspace.Path, "cache"));

        store.Save("entry-1", Entry());
        store.Delete("entry-1");
        Assert.IsNull(store.Get("entry-1"));

        store.Delete("entry-1");
    }
}
