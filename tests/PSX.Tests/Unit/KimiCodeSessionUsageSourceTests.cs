using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiCodeSessionUsageSourceTests
{
    private sealed class RecordingSink : IAgentUsageRecordSink
    {
        public List<AgentUsageRecord> Records { get; } = [];
        public void Add(AgentUsageRecord record) => Records.Add(record);
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

    private static void WriteWire(
        string home,
        string workDirKey,
        string sessionId,
        string agentId,
        params string[] lines)
    {
        var directory = Path.Combine(
            home, "sessions", workDirKey, sessionId, "agents", agentId);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "wire.jsonl"), lines);
    }

    private static (AgentUsageSourceStatus Status, List<AgentUsageRecord> Records) Collect(
        string home,
        params string[] sessionIds)
    {
        var source = new KimiCodeSessionUsageSource(() => home);
        var sink = new RecordingSink();
        var status = source.Collect(sessionIds, sink, CancellationToken.None);
        return (status, sink.Records);
    }

    [TestMethod]
    public void ResolveSessionId_UsesOnlyTheAcpIdentifier()
    {
        var source = new KimiCodeSessionUsageSource(() => null);
        var snapshot = new AgentUsageThreadSnapshot(
            "acp-kimi",
            ClaudeSessionId: "legacy-claude-id",
            AcpSessionId: "kimi-acp-id");

        Assert.AreEqual("kimi-acp-id", source.ResolveSessionId(snapshot));
        Assert.IsNull(source.ResolveSessionId(snapshot with { AcpSessionId = null }));
    }

    [TestMethod]
    public void Collect_Real0291Fixture_RecognizesPersistedWireShape()
    {
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "KimiUsage",
            "0.29.1");

        var result = Collect(fixtureRoot, "psx-kimi-fixture");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
        Assert.AreEqual(110, result.Records.Sum(record =>
            record.InputTokens
            + record.OutputTokens
            + record.CacheReadTokens
            + record.CacheCreationTokens));
    }

    [TestMethod]
    public void Collect_MainAndSubagentUsage_EmitsExactFourPartRecords()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MainAndSubagentUsage_EmitsExactFourPartRecords));
        const long firstTime = 1784522400000;
        WriteWire(workspace.Path, "work-a", "psx-session", "main",
            Metadata(), Usage(firstTime, 10, 20, 30, 40));
        WriteWire(workspace.Path, "work-a", "psx-session", "sub-1",
            Metadata(), Usage(firstTime + 1000, 1, 2, 3, 4));
        WriteWire(workspace.Path, "work-a", "outside-psx", "main",
            Metadata(), Usage(firstTime, 900, 900, 900, 900));

        var result = Collect(workspace.Path, "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
        Assert.AreEqual(10, result.Records[0].InputTokens);
        Assert.AreEqual(20, result.Records[0].OutputTokens);
        Assert.AreEqual(30, result.Records[0].CacheReadTokens);
        Assert.AreEqual(40, result.Records[0].CacheCreationTokens);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(firstTime),
            result.Records[0].Timestamp);
    }

    [TestMethod]
    public void Collect_UnsafeSessionId_CannotEscapeAWorkDirectory()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnsafeSessionId_CannotEscapeAWorkDirectory));
        var escapedAgentDirectory = Path.Combine(
            workspace.Path, "sessions", "escape", "agents", "main");
        Directory.CreateDirectory(escapedAgentDirectory);
        File.WriteAllLines(
            Path.Combine(escapedAgentDirectory, "wire.jsonl"),
            [Metadata(), Usage(1784522400000, 99, 0, 0, 0)]);

        var result = Collect(workspace.Path, @"..\escape");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_RecognizedSetupOnlySession_IsExactZero()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_RecognizedSetupOnlySession_IsExactZero));
        WriteWire(workspace.Path, "work-a", "empty-session", "main",
            Metadata(),
            """{"type":"config.update","time":1}""");

        var result = Collect(workspace.Path, "empty-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_ActivityWithoutUsage_IsUnsupportedAndUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ActivityWithoutUsage_IsUnsupportedAndUnavailable));
        WriteWire(workspace.Path, "work-a", "active-session", "main",
            Metadata(),
            """{"type":"turn.prompt","time":2,"input":[]}""");

        var result = Collect(workspace.Path, "active-session");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_UnreadableSubagent_KeepsRecordsButDoesNotClaimExactMatch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnreadableSubagent_KeepsRecordsButDoesNotClaimExactMatch));
        WriteWire(workspace.Path, "work-a", "partial-session", "main",
            Metadata(),
            Usage(1784522400000, 1, 2, 3, 4));
        WriteWire(workspace.Path, "work-a", "partial-session", "sub-1",
            Metadata(),
            "{ not-json");

        var result = Collect(workspace.Path, "partial-session");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_UnknownProtocolWithKnownUsageShape_RemainsAvailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnknownProtocolWithKnownUsageShape_RemainsAvailable));
        WriteWire(workspace.Path, "work-a", "future-session", "main",
            Metadata("9.0"),
            Usage(1784522400000, 1, 2, 3, 4));

        var result = Collect(workspace.Path, "future-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_DuplicateSessionDirectory_IsAmbiguousAndNeverDoubleCounts()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DuplicateSessionDirectory_IsAmbiguousAndNeverDoubleCounts));
        WriteWire(workspace.Path, "work-a", "duplicate-session", "main",
            Metadata(), Usage(1784522400000, 1, 2, 3, 4));
        WriteWire(workspace.Path, "work-b", "duplicate-session", "main",
            Metadata(), Usage(1784522400000, 1, 2, 3, 4));

        var result = Collect(workspace.Path, "duplicate-session");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.IsEmpty(result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.AmbiguousSessionLogs);
    }
}
