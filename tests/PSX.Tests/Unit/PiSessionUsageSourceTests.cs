using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class PiSessionUsageSourceTests
{
    private sealed class Sink : IAgentUsageRecordSink
    {
        public List<AgentUsageRecord> Records { get; } = [];
        public void Add(AgentUsageRecord record) => Records.Add(record);
    }

    [TestMethod]
    public void Collect_OnlyCountsOwnedSessionsAndDeduplicatesRepeatedRows()
    {
        using var workspace = TestWorkspace.Create(nameof(PiSessionUsageSourceTests));
        var row = Row("message-1");
        PiProviderTests.Write(workspace.Path, "project/date_owned.jsonl", Header("owned") + row + row);
        PiProviderTests.Write(workspace.Path, "project/date_foreign.jsonl", Header("foreign") + row);
        var sink = new Sink();
        var result = new PiSessionUsageSource(() => workspace.Path).Collect(["owned"], sink, default);
        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status);
        Assert.HasCount(1, sink.Records);
        Assert.AreEqual(10L, sink.Records[0].InputTokens);
        Assert.AreEqual(4L, sink.Records[0].CacheCreationTokens);
    }

    [TestMethod]
    public void Collect_ForkExcludesInheritedUsageEvenWhenParentIsNotOwned()
    {
        using var workspace = TestWorkspace.Create(nameof(PiSessionUsageSourceTests));
        var parent = Path.Combine(workspace.Path, "date_parent.jsonl");
        File.WriteAllText(parent, Header("parent") + Row("inherited"));
        PiProviderTests.Write(workspace.Path, "date_child.jsonl", Header("child", parent) + Row("inherited") + Row("new"));
        var sink = new Sink();
        var result = new PiSessionUsageSource(() => workspace.Path).Collect(["child"], sink, default);
        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status);
        Assert.HasCount(1, sink.Records);
    }

    [TestMethod]
    public void Collect_BadRowsKeepExactUsageButReportPartial()
    {
        using var workspace = TestWorkspace.Create(nameof(PiSessionUsageSourceTests));
        PiProviderTests.Write(workspace.Path, "date_owned.jsonl", Header("owned") + Row("good") + "not-json\n" + Row("bad", -1));
        var sink = new Sink();
        var result = new PiSessionUsageSource(() => workspace.Path).Collect(["owned"], sink, default);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status);
        Assert.HasCount(1, sink.Records);
        Assert.AreEqual(2, result.BadLines);
    }

    [TestMethod]
    public void Collect_MissingSessionIsUnavailable_EmptyScopeIsAvailable()
    {
        using var workspace = TestWorkspace.Create(nameof(PiSessionUsageSourceTests));
        var source = new PiSessionUsageSource(() => workspace.Path);
        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, source.Collect(["absent"], new Sink(), default).Status);
        Assert.AreEqual(AgentUsageSourceStatus.Available, source.Collect([], new Sink(), default).Status);
    }

    [TestMethod]
    public void Collect_UnresolvedForkDoesNotCountInheritedTokens()
    {
        using var workspace = TestWorkspace.Create(nameof(PiSessionUsageSourceTests));
        PiProviderTests.Write(workspace.Path, "date_owned.jsonl", Header("owned", Path.Combine(workspace.Path, "missing")) + Row("unknown"));
        var sink = new Sink();
        var result = new PiSessionUsageSource(() => workspace.Path).Collect(["owned"], sink, default);
        Assert.HasCount(0, sink.Records);
        CollectionAssert.Contains(result.Reasons.ToArray(), AgentUsageGapReason.LineageUnresolved);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("[]\n")]
    [DataRow("{\"type\":\"session\",\"version\":3,\"id\":\"owned\"}\n")]
    public void Collect_InvalidParentDoesNotTurnInheritedUsageIntoFreshUsage(string parentContent)
    {
        using var workspace = TestWorkspace.Create(nameof(PiSessionUsageSourceTests));
        var parent = Path.Combine(workspace.Path, "parent.jsonl");
        File.WriteAllText(parent, parentContent);
        PiProviderTests.Write(workspace.Path, "date_owned.jsonl", Header("owned", parent) + Row("unknown"));
        var sink = new Sink();
        var result = new PiSessionUsageSource(() => workspace.Path).Collect(["owned"], sink, default);
        Assert.HasCount(0, sink.Records);
        CollectionAssert.Contains(result.Reasons.ToArray(), AgentUsageGapReason.LineageUnresolved);
    }

    private static string Header(string id, string? parentSession = null) => JsonSerializer.Serialize(new
    { type = "session", version = 3, id, timestamp = "2026-09-15T00:00:00Z", parentSession }) + "\n";

    private static string Row(string id, long input = 10) => JsonSerializer.Serialize(new
    {
        type = "message",
        id,
        timestamp = "2026-09-15T01:00:00Z",
        message = new { role = "assistant", usage = new { input, output = 2, cacheRead = 3, cacheWrite = 4, totalTokens = input + 9 } }
    }) + "\n";
}
