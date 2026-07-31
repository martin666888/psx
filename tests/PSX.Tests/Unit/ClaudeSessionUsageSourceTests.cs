using System.IO;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ClaudeSessionUsageSourceTests
{
    private sealed class RecordingSink : IAgentUsageRecordSink
    {
        public List<AgentUsageRecord> Records { get; } = [];
        public void Add(AgentUsageRecord record) => Records.Add(record);
    }

    private static string WriteSession(string projectsDir, string sessionId, params string[] lines)
    {
        var projectDir = Path.Combine(projectsDir, "project-a");
        Directory.CreateDirectory(projectDir);
        var file = Path.Combine(projectDir, sessionId + ".jsonl");
        File.WriteAllLines(file, lines);
        return file;
    }

    private static string AssistantLine(
        string sessionId, string messageId, string model,
        long input, long output, long cacheRead, long cacheCreation, string timestamp)
    {
        var usage = "{\"input_tokens\":" + input
            + ",\"output_tokens\":" + output
            + ",\"cache_read_input_tokens\":" + cacheRead
            + ",\"cache_creation_input_tokens\":" + cacheCreation + "}";
        var message = "{\"id\":\"" + messageId + "\",\"role\":\"assistant\",\"model\":\""
            + model + "\",\"usage\":" + usage + "}";
        return "{\"type\":\"assistant\",\"isSidechain\":false,\"sessionId\":\"" + sessionId
            + "\",\"timestamp\":\"" + timestamp + "\",\"message\":" + message + "}";
    }

    private static ClaudeSessionUsageSource SourceFor(string configDir) =>
        new(() => configDir);

    private static (AgentUsageSourceStatus Status, List<AgentUsageRecord> Records) Collect(
        ClaudeSessionUsageSource source,
        IReadOnlyCollection<string> sessionIds)
    {
        var sink = new RecordingSink();
        var status = source.Collect(sessionIds, sink, CancellationToken.None);
        return (status, sink.Records);
    }

    [TestMethod]
    public void Collect_NoSessions_AvailableWithZeroData()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NoSessions_AvailableWithZeroData));
        // Deliberately do NOT create a projects dir: no PSX Claude sessions must
        // still be "available" (zero), not "unavailable".
        var source = SourceFor(Path.Combine(workspace.Path, "missing-claude"));

        var contribution = Collect(source, []);

        Assert.AreEqual(AgentUsageSourceStatus.Available, contribution.Status.Status);
        Assert.IsEmpty(contribution.Records);
        Assert.AreEqual(0, contribution.Status.ExpectedSessions);
    }

    [TestMethod]
    public void Collect_DirectoryMissing_Unavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DirectoryMissing_Unavailable));
        var source = SourceFor(Path.Combine(workspace.Path, "missing-claude"));

        var contribution = Collect(source, ["session-1"]);

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, contribution.Status.Status);
        Assert.AreEqual(1, contribution.Status.ExpectedSessions);
        Assert.AreEqual(0, contribution.Status.MatchedSessions);
        Assert.IsFalse(string.IsNullOrWhiteSpace(contribution.Status.Detail));
    }

    [TestMethod]
    public void Collect_MatchesOnlyPsxSessions_ParsesUsageAndCacheHits()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MatchesOnlyPsxSessions_ParsesUsageAndCacheHits));
        var configDir = Path.Combine(workspace.Path, ".claude");
        var projectsDir = Path.Combine(configDir, "projects");
        WriteSession(projectsDir, "psx-session",
            AssistantLine("psx-session", "m1", "claude-x", 100, 20, 4000, 50, "2026-07-20T10:00:00Z"));
        // A session PSX did not create must be ignored entirely.
        WriteSession(projectsDir, "other-session",
            AssistantLine("other-session", "m9", "claude-x", 999, 999, 999, 999, "2026-07-20T10:00:00Z"));

        var contribution = Collect(SourceFor(configDir), ["psx-session"]);

        Assert.AreEqual(AgentUsageSourceStatus.Available, contribution.Status.Status);
        Assert.HasCount(1, contribution.Records);
        var record = contribution.Records[0];
        Assert.AreEqual(100, record.InputTokens);
        Assert.AreEqual(20, record.OutputTokens);
        Assert.AreEqual(4000, record.CacheReadTokens);
        Assert.AreEqual(50, record.CacheCreationTokens);
        Assert.AreEqual("claude-x", record.Model);
        Assert.AreEqual(1, contribution.Status.MatchedSessions);
    }

    [TestMethod]
    public void Collect_UnsafeSessionId_CannotEscapeTheProjectsDirectory()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UnsafeSessionId_CannotEscapeTheProjectsDirectory));
        var configDir = Path.Combine(workspace.Path, ".claude");
        var projectsDir = Path.Combine(configDir, "projects");
        Directory.CreateDirectory(projectsDir);
        File.WriteAllLines(
            Path.Combine(configDir, "escape.jsonl"),
            [AssistantLine("escape", "m1", "claude-x", 99, 0, 0, 0, "2026-07-20T10:00:00Z")]);

        var contribution = Collect(SourceFor(configDir), [@"..\escape"]);

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, contribution.Status.Status);
        Assert.AreEqual(0, contribution.Status.MatchedSessions);
        Assert.IsEmpty(contribution.Records);
    }

    [TestMethod]
    public void Collect_DeduplicatesByMessageId()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DeduplicatesByMessageId));
        var configDir = Path.Combine(workspace.Path, ".claude");
        var projectsDir = Path.Combine(configDir, "projects");
        WriteSession(projectsDir, "psx-session",
            AssistantLine("psx-session", "dup", "claude-x", 10, 5, 0, 0, "2026-07-20T10:00:00Z"),
            AssistantLine("psx-session", "dup", "claude-x", 10, 5, 0, 0, "2026-07-20T10:00:01Z"));

        var contribution = Collect(SourceFor(configDir), ["psx-session"]);

        Assert.HasCount(1, contribution.Records);
    }

    [TestMethod]
    public void Collect_BadLine_PreventsAnExactSessionMatch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_BadLine_PreventsAnExactSessionMatch));
        var configDir = Path.Combine(workspace.Path, ".claude");
        var projectsDir = Path.Combine(configDir, "projects");
        WriteSession(projectsDir, "psx-session",
            AssistantLine("psx-session", "m1", "claude-x", 10, 5, 0, 0, "2026-07-20T10:00:00Z"),
            "{ this is not valid json",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\"}}");

        var contribution = Collect(SourceFor(configDir), ["psx-session"]);

        Assert.HasCount(1, contribution.Records);
        Assert.AreEqual(1, contribution.Status.BadLines);
        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, contribution.Status.Status);
        Assert.AreEqual(0, contribution.Status.MatchedSessions);
    }

    [TestMethod]
    public void Collect_PartialSessionMatch_ReportsExpectedVsMatched()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_PartialSessionMatch_ReportsExpectedVsMatched));
        var configDir = Path.Combine(workspace.Path, ".claude");
        var projectsDir = Path.Combine(configDir, "projects");
        WriteSession(projectsDir, "psx-a",
            AssistantLine("psx-a", "m1", "claude-x", 10, 5, 0, 0, "2026-07-20T10:00:00Z"));

        // Two expected, only one present on disk.
        var contribution = Collect(SourceFor(configDir), ["psx-a", "psx-b"]);

        Assert.AreEqual(AgentUsageSourceStatus.Partial, contribution.Status.Status);
        Assert.AreEqual(2, contribution.Status.ExpectedSessions);
        Assert.AreEqual(1, contribution.Status.MatchedSessions);
    }
}
