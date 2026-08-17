using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QwenCodeSessionUsageSourceTests
{
    private const string Timestamp1 = "2026-07-20T10:00:00Z";
    private const string Timestamp2 = "2026-07-21T11:00:00Z";

    private sealed class RecordingSink : IAgentUsageRecordSink
    {
        public List<AgentUsageRecord> Records { get; } = [];
        public void Add(AgentUsageRecord record) => Records.Add(record);
    }

    private static string Monthly(
        string? id,
        string sessionId,
        string timestamp,
        string model,
        long input,
        long output,
        long cached,
        long thoughts,
        long total,
        string? source = null,
        int schemaVersion = 1)
    {
        var idJson = id == null ? string.Empty : ",\"id\":\"" + id + "\"";
        return "{\"schemaVersion\":" + schemaVersion
            + idJson
            + ",\"timestamp\":\"" + timestamp + "\""
            + ",\"localDate\":\"2026-07-20\",\"localMonth\":\"2026-07\""
            + ",\"sessionId\":\"" + sessionId + "\""
            + ",\"model\":\"" + model + "\""
            + ",\"authType\":\"unknown\""
            + ",\"source\":\"" + (source ?? "main") + "\""
            + ",\"inputTokens\":" + input
            + ",\"outputTokens\":" + output
            + ",\"cachedTokens\":" + cached
            + ",\"thoughtsTokens\":" + thoughts
            + ",\"totalTokens\":" + total
            + ",\"apiDurationMs\":0}";
    }

    private static string ChatAssistant(
        string sessionId,
        string model,
        long prompt,
        long candidates,
        long cached,
        long thoughts,
        long total)
    {
        return "{\"uuid\":\"" + Guid.NewGuid().ToString("N") + "\""
            + ",\"parentUuid\":null"
            + ",\"sessionId\":\"" + sessionId + "\""
            + ",\"timestamp\":\"" + Timestamp1 + "\""
            + ",\"type\":\"assistant\""
            + ",\"provenance\":\"assistant_output\""
            + ",\"cwd\":\"D:\\\\psx\""
            + ",\"version\":\"0.21.5\""
            + ",\"gitBranch\":null"
            + ",\"model\":\"" + model + "\""
            + ",\"message\":{\"role\":\"assistant\",\"parts\":[{\"text\":\"ok\"}]}"
            + ",\"usageMetadata\":{\"promptTokenCount\":" + prompt
            + ",\"totalTokenCount\":" + total
            + ",\"candidatesTokenCount\":" + candidates
            + ",\"cachedContentTokenCount\":" + cached
            + ",\"thoughtsTokenCount\":" + thoughts + "}}";
    }

    private static string ChatUser(string sessionId) =>
        "{\"uuid\":\"u-1\",\"parentUuid\":null,\"sessionId\":\"" + sessionId
        + "\",\"timestamp\":\"" + Timestamp1 + "\""
        + ",\"type\":\"user\""
        + ",\"provenance\":\"real_user\",\"cwd\":\"D:\\\\psx\",\"version\":\"0.21.5\""
        + ",\"gitBranch\":null,\"message\":{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}}";

    private static void WriteUsage(string root, string month, params string[] lines)
    {
        var directory = Path.Combine(root, "usage");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "token-usage-" + month + ".jsonl"), lines);
    }

    private static string WriteChat(
        string root,
        string projectKey,
        string sessionId,
        params string[] lines)
    {
        var directory = Path.Combine(root, "projects", projectKey, "chats");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string WriteSidecar(string root, string projectKey, string sessionId)
    {
        var directory = Path.Combine(root, "projects", projectKey, "chats");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId + ".runtime.json");
        File.WriteAllText(path, "{\"state\":\"active\"}");
        return path;
    }

    private static (AgentUsageSourceStatus Status, List<AgentUsageRecord> Records) Collect(
        QwenCodeSessionUsageSource source,
        params string[] sessionIds)
    {
        var sink = new RecordingSink();
        var status = source.Collect(sessionIds, sink, CancellationToken.None);
        return (status, sink.Records);
    }

    private static QwenRuntimeRootResolution Resolve(
        string? runtimeDir,
        string? qwenHome,
        Func<string?> homeResolver) =>
        QwenCodeSessionUsageSource.ResolveRuntimeRoots(
            key => key switch
            {
                "QWEN_RUNTIME_DIR" => runtimeDir,
                "QWEN_HOME" => qwenHome,
                _ => null
            },
            homeResolver);

    [TestMethod]
    public void ResolveSessionId_UsesOnlyTheAcpIdentifier()
    {
        var source = new QwenCodeSessionUsageSource(() => Array.Empty<string>());
        var snapshot = new AgentUsageThreadSnapshot(
            "acp-qwen",
            ClaudeSessionId: "legacy-claude-id",
            AcpSessionId: "qwen-acp-id");

        Assert.AreEqual("qwen-acp-id", source.ResolveSessionId(snapshot));
        Assert.IsNull(source.ResolveSessionId(snapshot with { AcpSessionId = null }));
    }

    [TestMethod]
    public void Collect_IgnoresNonPsxMonthlyRecords()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_IgnoresNonPsxMonthlyRecords));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130),
            Monthly("m-2", "outside-psx", Timestamp1, "qwen-max", 999, 999, 0, 0, 1998));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(0, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_NormalizesInputMinusCached_WithBothTotalRelations()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NormalizesInputMinusCached_WithBothTotalRelations));
        var root = workspace.Path;
        // Relation one: total == input + output (thoughts 0). Relation two:
        // total == input + output + thoughts, and input == cached -> net 0.
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130),
            Monthly("m-2", "psx-session", Timestamp2, "qwen-plus", 200, 30, 200, 7, 237));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130),
            ChatAssistant("psx-session", "qwen-plus", 200, 30, 200, 7, 237));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(2, result.Records);
        Assert.AreEqual(10, result.Records[0].InputTokens);
        Assert.AreEqual(20, result.Records[0].OutputTokens);
        Assert.AreEqual(100, result.Records[0].CacheReadTokens);
        Assert.AreEqual(0, result.Records[0].CacheCreationTokens);
        Assert.AreEqual(0, result.Records[1].InputTokens);
        Assert.AreEqual(37, result.Records[1].OutputTokens);
        Assert.AreEqual(200, result.Records[1].CacheReadTokens);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            result.Records[0].Timestamp);
    }

    [TestMethod]
    public void Collect_ThirdTotalRelation_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ThirdTotalRelation_CountsBadLine));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 10, 20, 0, 3, 100),
            Monthly("m-2", "psx-session", Timestamp2, "qwen-plus", 50, 10, 0, 0, 60));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 50, 10, 0, 0, 60));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
    }

    [TestMethod]
    public void Collect_NearLongMaxValue_DoesNotFalsePositiveOverflow()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NearLongMaxValue_DoesNotFalsePositiveOverflow));
        var root = workspace.Path;
        const long nearMax = long.MaxValue - 1;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", nearMax, 1, 0, 0, long.MaxValue));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", nearMax, 1, 0, 0, long.MaxValue));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(nearMax, result.Records[0].InputTokens);
        Assert.AreEqual(1, result.Records[0].OutputTokens);
    }

    [TestMethod]
    public void Collect_OverflowingTokenSum_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OverflowingTokenSum_CountsBadLine));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", long.MaxValue, 1, 0, 0, 1));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.IsEmpty(result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
    }

    [TestMethod]
    public void Collect_CachedGreaterThanInput_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CachedGreaterThanInput_CountsBadLine));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 100, 10, 150, 0, 110));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.IsEmpty(result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
    }

    [TestMethod]
    public void Collect_SameIdAcrossRoots_IsDeduplicated()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SameIdAcrossRoots_IsDeduplicated));
        var rootA = Path.Combine(workspace.Path, "a");
        var rootB = Path.Combine(workspace.Path, "b");
        WriteUsage(rootA, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        WriteUsage(rootB, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        WriteChat(rootA, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(
            new QwenCodeSessionUsageSource(() => [rootA, rootB]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(0, result.Status.BadLines);
    }

    [TestMethod]
    public void Collect_ConflictingIdAcrossRoots_RecordsUnsupportedFormat()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ConflictingIdAcrossRoots_RecordsUnsupportedFormat));
        var rootA = Path.Combine(workspace.Path, "a");
        var rootB = Path.Combine(workspace.Path, "b");
        WriteUsage(rootA, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        WriteUsage(rootB, "2026-07",
            Monthly("m-1", "psx-session", Timestamp2, "qwen-plus", 999, 999, 0, 0, 1998));
        WriteChat(rootA, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(
            new QwenCodeSessionUsageSource(() => [rootA, rootB]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_MissingId_RecordsUnsupportedFormat()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingId_RecordsUnsupportedFormat));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly(null, "psx-session", Timestamp1, "qwen-plus", 10, 20, 0, 0, 30),
            Monthly("m-2", "psx-session", Timestamp2, "qwen-plus", 50, 10, 0, 0, 60));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 50, 10, 0, 0, 60));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_CrossMonthMultipleModelsAndSubagents_AllCounted()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CrossMonthMultipleModelsAndSubagents_AllCounted));
        var root = workspace.Path;
        WriteUsage(root, "2026-06",
            Monthly("m-1", "psx-session", "2026-06-10T10:00:00Z", "qwen-max", 100, 20, 0, 0, 120));
        WriteUsage(root, "2026-07",
            Monthly("m-2", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130),
            Monthly("m-3", "psx-session", Timestamp2, "qwen-plus", 200, 30, 0, 5, 235, source: "sub-task"));
        // The chat only covers the two main turns; the subagent row stays a
        // legal monthly-only record (monthly ⊋ chat).
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-max", 100, 20, 0, 0, 120),
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(3, result.Records);
        Assert.AreEqual("qwen-max", result.Records[0].Model);
        Assert.AreEqual("qwen-plus", result.Records[1].Model);
        Assert.AreEqual(0, result.Status.BadLines);
    }

    [TestMethod]
    public void Collect_SchemaVersionTwo_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SchemaVersionTwo_CountsBadLine));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 10, 20, 0, 0, 30, schemaVersion: 2),
            Monthly("m-2", "psx-session", Timestamp2, "qwen-plus", 50, 10, 0, 0, 60));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 50, 10, 0, 0, 60));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
    }

    [TestMethod]
    public void Collect_OversizedMonthlyLine_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OversizedMonthlyLine_CountsBadLine));
        var root = workspace.Path;
        var hugeBlob = new string('a', 16 * 1024 * 1024);
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 50, 10, 0, 0, 60),
            "{\"schemaVersion\":1,\"blob\":\"" + hugeBlob + "\"}");
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 50, 10, 0, 0, 60));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
    }

    [TestMethod]
    public void Collect_ChatSubsetOfMonthly_IsMatched()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ChatSubsetOfMonthly_IsMatched));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130),
            Monthly("m-2", "psx-session", Timestamp2, "qwen-plus", 200, 30, 0, 0, 230));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
    }

    [TestMethod]
    public void Collect_ChatSignatureNotInMonthly_IsPartialAndKeepsRecords()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ChatSignatureNotInMonthly_IsPartialAndKeepsRecords));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-a", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130),
            Monthly("m-2", "psx-b", Timestamp2, "qwen-plus", 200, 30, 0, 0, 230));
        WriteChat(root, "proj-a", "psx-a",
            ChatAssistant("psx-a", "qwen-plus", 110, 20, 100, 0, 130),
            ChatAssistant("psx-a", "qwen-plus", 500, 50, 0, 0, 550));
        WriteChat(root, "proj-a", "psx-b",
            ChatAssistant("psx-b", "qwen-plus", 200, 30, 0, 0, 230));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-a", "psx-b");

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(2, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnmatchedSessions);
    }

    [TestMethod]
    public void Collect_ZeroUsageChatWithSidecar_IsExactZeroMatch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ZeroUsageChatWithSidecar_IsExactZeroMatch));
        var root = workspace.Path;
        WriteChat(root, "proj-a", "psx-session", ChatUser("psx-session"));
        WriteSidecar(root, "proj-a", "psx-session");

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_SidecarOnly_IsExactZeroMatch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SidecarOnly_IsExactZeroMatch));
        var root = workspace.Path;
        WriteSidecar(root, "proj-a", "psx-session");

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_NoUsageDirectories_IsUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NoUsageDirectories_IsUnavailable));
        var result = Collect(
            new QwenCodeSessionUsageSource(() => [workspace.Path]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.MissingSessionLogs);
    }

    [TestMethod]
    public void Collect_CorruptChat_IsUnmatchedWithUnreadableLogs()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptChat_IsUnmatchedWithUnreadableLogs));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        var chatPath = WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));
        File.AppendAllText(chatPath, "{ this is broken json\n");

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(1, result.Status.BadLines);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_MonthlyWithMissingChat_IsMatched()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MonthlyWithMissingChat_IsMatched));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_MultipleChatLocations_IsAmbiguousAndUnmatched()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MultipleChatLocations_IsAmbiguousAndUnmatched));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));
        WriteChat(root, "proj-b", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.AmbiguousSessionLogs);
    }

    [TestMethod]
    public void Collect_UnterminatedFinalMonthlyLine_IsNotCountedAsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnterminatedFinalMonthlyLine_IsNotCountedAsBadLine));
        var root = workspace.Path;
        var usageDir = Path.Combine(root, "usage");
        Directory.CreateDirectory(usageDir);
        File.WriteAllText(
            Path.Combine(usageDir, "token-usage-2026-07.jsonl"),
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130)
            + "\n{\"schemaVersion\":1,\"id\":\"half");
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(0, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_UnterminatedFinalChatLine_IsNotCountedAsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnterminatedFinalChatLine_IsNotCountedAsBadLine));
        var root = workspace.Path;
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        var chatDir = Path.Combine(root, "projects", "proj-a", "chats");
        Directory.CreateDirectory(chatDir);
        File.WriteAllText(
            Path.Combine(chatDir, "psx-session.jsonl"),
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130)
            + "\n{\"type\":\"assistant\",\"model\":\"qwen-p");

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(0, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_UnsafeSessionId_CannotEscapeTheChatsDirectory()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnsafeSessionId_CannotEscapeTheChatsDirectory));
        var root = workspace.Path;
        // A decoy chat file sits one level above the chats dir; only a path
        // traversal through the session id could reach it.
        var decoy = Path.Combine(root, "projects", "proj-a", "escape.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        File.WriteAllLines(decoy,
            [ChatAssistant("escape", "qwen-plus", 99, 0, 0, 0, 99)]);

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), @"..\escape");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
    }

    [TestMethod]
    public void Collect_OverlongSessionId_DoesNotBreakTheScan()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OverlongSessionId_DoesNotBreakTheScan));
        var root = workspace.Path;
        var overlongId = new string('x', 3000);

        var result = Collect(new QwenCodeSessionUsageSource(() => [root]), overlongId);

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.IsEmpty(result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.MissingSessionLogs);
        CollectionAssert.DoesNotContain(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void ResolveRuntimeRoots_RuntimeDirAbsolute_IsIncludedBeforeDefault()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_RuntimeDirAbsolute_IsIncludedBeforeDefault));
        var runtimeDir = Path.Combine(workspace.Path, "runtime");
        var home = Path.Combine(workspace.Path, "home");

        var resolution = Resolve(runtimeDir, null, () => home);

        Assert.HasCount(2, resolution.Roots);
        Assert.AreEqual(runtimeDir, resolution.Roots[0]);
        Assert.AreEqual(home, resolution.Roots[1]);
        Assert.IsFalse(resolution.HasGap);
    }

    [TestMethod]
    public void ResolveRuntimeRoots_RuntimeDirRelative_IsSkippedWithGap()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_RuntimeDirRelative_IsSkippedWithGap));
        var home = Path.Combine(workspace.Path, "home");

        var resolution = Resolve("./relative-runtime", null, () => home);

        Assert.IsTrue(resolution.SkippedRelativeRuntimeDir);
        Assert.IsTrue(resolution.HasGap);
        CollectionAssert.DoesNotContain(resolution.Roots.ToArray(), "./relative-runtime");
        Assert.HasCount(1, resolution.Roots);
    }

    [TestMethod]
    public void ResolveRuntimeRoots_SettingsAbsoluteRuntimeOutputDir_IsIncluded()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_SettingsAbsoluteRuntimeOutputDir_IsIncluded));
        var home = Path.Combine(workspace.Path, "home");
        var settingsDir = Path.Combine(workspace.Path, "settings-runtime");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, "settings.json"),
            JsonSerializer.Serialize(new { advanced = new { runtimeOutputDir = settingsDir } }));

        var resolution = Resolve(null, null, () => home);

        CollectionAssert.Contains(resolution.Roots.ToArray(), settingsDir);
        Assert.IsFalse(resolution.SettingsNotUsable);
        Assert.IsFalse(resolution.HasGap);
    }

    [TestMethod]
    public void ResolveRuntimeRoots_SettingsRelativeRuntimeOutputDir_IsSkippedWithGap()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_SettingsRelativeRuntimeOutputDir_IsSkippedWithGap));
        var home = Path.Combine(workspace.Path, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, "settings.json"),
            JsonSerializer.Serialize(new { advanced = new { runtimeOutputDir = "out/runtime" } }));

        var resolution = Resolve(null, null, () => home);

        Assert.IsTrue(resolution.SkippedRelativeRuntimeDir);
        Assert.IsTrue(resolution.HasGap);
        CollectionAssert.DoesNotContain(resolution.Roots.ToArray(), "out/runtime");
    }

    [TestMethod]
    public void ResolveRuntimeRoots_OversizedSettings_IsNotUsable()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_OversizedSettings_IsNotUsable));
        var home = Path.Combine(workspace.Path, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, "settings.json"),
            "{\"advanced\":" + new string(' ', 2 * 1024 * 1024 + 1) + "}");

        var resolution = Resolve(null, null, () => home);

        Assert.IsTrue(resolution.SettingsNotUsable);
        Assert.IsTrue(resolution.HasGap);
    }

    [TestMethod]
    public void ResolveRuntimeRoots_JsoncSettings_IsParsed()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_JsoncSettings_IsParsed));
        var home = Path.Combine(workspace.Path, "home");
        var settingsDir = Path.Combine(workspace.Path, "settings-runtime");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, "settings.json"),
            "{\n  // runtime output location\n  \"advanced\": {\n"
            + "    \"runtimeOutputDir\": \"" + settingsDir.Replace("\\", "\\\\") + "\",\n"
            + "  },\n}\n");

        var resolution = Resolve(null, null, () => home);

        CollectionAssert.Contains(resolution.Roots.ToArray(), settingsDir);
        Assert.IsFalse(resolution.SettingsNotUsable);
    }

    [TestMethod]
    public void ResolveRuntimeRoots_DeduplicatesAndOrdersCandidates()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveRuntimeRoots_DeduplicatesAndOrdersCandidates));
        var runtimeDir = Path.Combine(workspace.Path, "runtime");
        var home = Path.Combine(workspace.Path, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, "settings.json"),
            JsonSerializer.Serialize(new { advanced = new { runtimeOutputDir = runtimeDir } }));

        var resolution = Resolve(runtimeDir, null, () => home);

        Assert.HasCount(2, resolution.Roots);
        Assert.AreEqual(runtimeDir, resolution.Roots[0]);
        Assert.AreEqual(home, resolution.Roots[1]);
    }

    [TestMethod]
    public void Collect_RelativeRuntimeConfig_NeverClaimsCleanAvailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_RelativeRuntimeConfig_NeverClaimsCleanAvailable));
        var root = Path.Combine(workspace.Path, "runtime");
        WriteUsage(root, "2026-07",
            Monthly("m-1", "psx-session", Timestamp1, "qwen-plus", 110, 20, 100, 0, 130));
        WriteChat(root, "proj-a", "psx-session",
            ChatAssistant("psx-session", "qwen-plus", 110, 20, 100, 0, 130));
        var source = new QwenCodeSessionUsageSource(() =>
            new QwenRuntimeRootResolution(
                [root], SkippedRelativeRuntimeDir: true, SettingsNotUsable: false));

        var result = Collect(source, "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
        CollectionAssert.Contains(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_Real0215Fixture_RecognizesPersistedUsageShape()
    {
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "QwenUsage",
            "0.21.5");

        var result = Collect(
            new QwenCodeSessionUsageSource(() => [fixtureRoot]), "psx-qwen-fixture");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(0, result.Status.BadLines);
        Assert.HasCount(2, result.Records);
        Assert.AreEqual(10, result.Records[0].InputTokens);
        Assert.AreEqual(20, result.Records[0].OutputTokens);
        Assert.AreEqual(100, result.Records[0].CacheReadTokens);
        Assert.AreEqual(365, result.Records.Sum(record =>
            record.InputTokens
            + record.OutputTokens
            + record.CacheReadTokens
            + record.CacheCreationTokens));
    }
}
