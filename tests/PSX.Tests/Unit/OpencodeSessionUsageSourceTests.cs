using Microsoft.Data.Sqlite;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class OpencodeSessionUsageSourceTests
{
    private sealed class RecordingSink : IAgentUsageRecordSink
    {
        public List<AgentUsageRecord> Records { get; } = [];
        public void Add(AgentUsageRecord record) => Records.Add(record);
    }

    private static long Ms(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static SqliteConnection CreateDatabase(string path)
    {
        // Pooling must stay off: pooled connections keep the database file
        // open after Dispose, which would break workspace cleanup (and the
        // "connection released, file deletable" contract under test).
        var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE session (
                    id TEXT PRIMARY KEY,
                    parent_id TEXT,
                    tokens_input INTEGER NOT NULL DEFAULT 0,
                    tokens_output INTEGER NOT NULL DEFAULT 0,
                    tokens_reasoning INTEGER NOT NULL DEFAULT 0,
                    tokens_cache_read INTEGER NOT NULL DEFAULT 0,
                    tokens_cache_write INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE message (
                    id TEXT PRIMARY KEY,
                    session_id TEXT,
                    time_created INTEGER,
                    data TEXT
                );
                """;
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static void InsertSession(
        SqliteConnection connection,
        string id,
        string? parentId,
        long input,
        long output,
        long reasoning,
        long cacheRead,
        long cacheWrite)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session (id, parent_id, tokens_input, tokens_output,
                tokens_reasoning, tokens_cache_read, tokens_cache_write)
            VALUES ($id, $parent, $input, $output, $reasoning, $cacheRead, $cacheWrite)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$input", input);
        command.Parameters.AddWithValue("$output", output);
        command.Parameters.AddWithValue("$reasoning", reasoning);
        command.Parameters.AddWithValue("$cacheRead", cacheRead);
        command.Parameters.AddWithValue("$cacheWrite", cacheWrite);
        command.ExecuteNonQuery();
    }

    private static void InsertMessage(
        SqliteConnection connection,
        string id,
        string sessionId,
        long timeCreated,
        string data)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message (id, session_id, time_created, data)
            VALUES ($id, $sessionId, $timeCreated, $data)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$timeCreated", timeCreated);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    private static string AssistantData(
        string? model,
        long input,
        long output,
        long reasoning,
        long cacheRead,
        long cacheWrite,
        string role = "assistant")
    {
        var modelJson = model == null ? string.Empty : ",\"modelID\":\"" + model + "\"";
        return "{\"role\":\"" + role + "\"" + modelJson
            + ",\"tokens\":{\"input\":" + input
            + ",\"output\":" + output
            + ",\"reasoning\":" + reasoning
            + ",\"cache\":{\"read\":" + cacheRead + ",\"write\":" + cacheWrite + "}}}";
    }

    private static (AgentUsageSourceStatus Status, List<AgentUsageRecord> Records) Collect(
        OpencodeSessionUsageSource source,
        params string[] sessionIds)
    {
        var sink = new RecordingSink();
        var status = source.Collect(sessionIds, sink, CancellationToken.None);
        return (status, sink.Records);
    }

    private static void AssertReason(
        AgentUsageSourceStatus status,
        string reason) =>
        CollectionAssert.Contains(status.Reasons.ToArray(), reason);

    [TestMethod]
    public void ResolveSessionId_UsesOnlyTheAcpIdentifier()
    {
        var source = new OpencodeSessionUsageSource(() => null);
        var snapshot = new AgentUsageThreadSnapshot(
            "acp-opencode",
            ClaudeSessionId: "legacy-claude-id",
            AcpSessionId: "opencode-acp-id");

        Assert.AreEqual("opencode-acp-id", source.ResolveSessionId(snapshot));
        Assert.IsNull(source.ResolveSessionId(snapshot with { AcpSessionId = null }));
    }

    [TestMethod]
    public void Collect_MapsFiveFieldsAndMatchesSessionAggregates_IsAvailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MapsFiveFieldsAndMatchesSessionAggregates_IsAvailable));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            // Two assistant messages; session aggregates equal their sums:
            // 110+20+5+100+2 = 237 and 50+10+0+0+0 = 60, total 297.
            InsertSession(db, "s-1", null, 160, 30, 5, 100, 2);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("opencode-1", 110, 20, 5, 100, 2));
            InsertMessage(db, "m-2", "s-1", Ms(2026, 7, 21),
                AssistantData("opencode-1", 50, 10, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(0, result.Status.BadLines);
        Assert.HasCount(2, result.Records);
        Assert.AreEqual("opencode-1", result.Records[0].Model);
        Assert.AreEqual(110, result.Records[0].InputTokens);
        Assert.AreEqual(25, result.Records[0].OutputTokens);
        Assert.AreEqual(100, result.Records[0].CacheReadTokens);
        Assert.AreEqual(2, result.Records[0].CacheCreationTokens);
        Assert.AreEqual(50, result.Records[1].InputTokens);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
            result.Records[0].Timestamp);
    }

    [TestMethod]
    public void Collect_MessageSumMismatch_IsPartialWithDataKept()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MessageSumMismatch_IsPartialWithDataKept));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            // Session aggregates claim 500 while the messages only sum to 297.
            InsertSession(db, "s-1", null, 400, 50, 30, 15, 5);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("opencode-1", 110, 20, 5, 100, 2));
            InsertMessage(db, "m-2", "s-1", Ms(2026, 7, 21),
                AssistantData("opencode-1", 50, 10, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
        AssertReason(result.Status, AgentUsageGapReason.UnsupportedFormat);
        CollectionAssert.DoesNotContain(
            result.Status.Reasons.ToArray(),
            AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_DescendantSessions_FollowParentIdRecursively()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DescendantSessions_FollowParentIdRecursively));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "root", null, 100, 0, 0, 0, 0);
            InsertSession(db, "child", "root", 40, 0, 0, 0, 0);
            InsertSession(db, "grandchild", "child", 10, 0, 0, 0, 0);
            InsertMessage(db, "m-root", "root", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-child", "child", Ms(2026, 7, 20),
                AssistantData("m", 40, 0, 0, 0, 0));
            InsertMessage(db, "m-grandchild", "grandchild", Ms(2026, 7, 20),
                AssistantData("m", 10, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "root");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(3, result.Records);
    }

    [TestMethod]
    public void Collect_ChildThatIsAlsoPsxRoot_IsCountedOnce()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ChildThatIsAlsoPsxRoot_IsCountedOnce));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "root", null, 100, 0, 0, 0, 0);
            InsertSession(db, "child", "root", 40, 0, 0, 0, 0);
            InsertMessage(db, "m-root", "root", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-child", "child", Ms(2026, 7, 20),
                AssistantData("m", 40, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "root", "child");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(2, result.Status.ExpectedSessions);
        Assert.AreEqual(2, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
    }

    [TestMethod]
    public void Collect_SelfReferentialParent_DoesNotLoop()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SelfReferentialParent_DoesNotLoop));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "loop", "loop", 100, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "loop", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "loop");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_TwoNodeParentCycle_DoesNotLoop()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_TwoNodeParentCycle_DoesNotLoop));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "a", "b", 60, 0, 0, 0, 0);
            InsertSession(db, "b", "a", 40, 0, 0, 0, 0);
            InsertMessage(db, "m-a", "a", Ms(2026, 7, 20), AssistantData("m", 60, 0, 0, 0, 0));
            InsertMessage(db, "m-b", "b", Ms(2026, 7, 20), AssistantData("m", 40, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "a");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(2, result.Records);
    }

    [TestMethod]
    public void Collect_OverFourHundredSessions_BatchesInQueries()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OverFourHundredSessions_BatchesInQueries));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            for (var index = 0; index < 450; index++)
            {
                InsertSession(db, "s-" + index, null, 1, 0, 0, 0, 0);
                InsertMessage(db, "m-" + index, "s-" + index, Ms(2026, 7, 20),
                    AssistantData("m", 1, 0, 0, 0, 0));
            }
        }

        var ids = Enumerable.Range(0, 450).Select(index => "s-" + index).ToArray();
        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), ids);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(450, result.Status.ExpectedSessions);
        Assert.AreEqual(450, result.Status.MatchedSessions);
        Assert.HasCount(450, result.Records);
        Assert.AreEqual(0, result.Status.BadLines);
    }

    [TestMethod]
    public void Collect_NonPsxSessions_AreExcluded()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NonPsxSessions_AreExcluded));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "psx-session", null, 100, 0, 0, 0, 0);
            InsertSession(db, "outside-psx", null, 999, 0, 0, 0, 0);
            InsertMessage(db, "m-psx", "psx-session", Ms(2026, 7, 20),
                AssistantData("psx-model", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-other", "outside-psx", Ms(2026, 7, 20),
                AssistantData("other-model", 999, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "psx-session");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual("psx-model", result.Records[0].Model);
    }

    [TestMethod]
    public void Collect_RootMissingFromSessionTable_IsUnmatched()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_RootMissingFromSessionTable_IsUnmatched));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "present", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-present", "present", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
        }

        var result = Collect(
            new OpencodeSessionUsageSource(() => dbPath), "present", "ghost");

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(2, result.Status.ExpectedSessions);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
        AssertReason(result.Status, AgentUsageGapReason.UnmatchedSessions);
    }

    [TestMethod]
    public void Collect_SchemaMissingColumn_IsUnsupportedFormat()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SchemaMissingColumn_IsUnsupportedFormat));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE message DROP COLUMN data";
            command.ExecuteNonQuery();
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
        AssertReason(result.Status, AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_MissingDatabase_IsUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingDatabase_IsUnavailable));
        var dbPath = Path.Combine(workspace.Path, "does-not-exist.db");

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(1, result.Status.ExpectedSessions);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
        AssertReason(result.Status, AgentUsageGapReason.MissingSessionLogs);
    }

    [TestMethod]
    public void Collect_CorruptDatabase_IsUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptDatabase_IsUnavailable));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        File.WriteAllText(dbPath, "this is definitely not a sqlite database");

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
        AssertReason(result.Status, AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_InvalidJsonMessage_CountsBadLineAndKeepsValidRows()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_InvalidJsonMessage_CountsBadLineAndKeepsValidRows));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-1", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-bad", "s-1", Ms(2026, 7, 20), "not json at all");
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
        AssertReason(result.Status, AgentUsageGapReason.UnreadableLogs);
    }

    [TestMethod]
    public void Collect_ZeroMessageSession_IsExactZeroMatch()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ZeroMessageSession_IsExactZeroMatch));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-empty", null, 0, 0, 0, 0, 0);
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-empty");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
        Assert.AreEqual(0, result.Status.BadLines);
    }

    [TestMethod]
    public void Collect_MissingModelId_UsesUnknown()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingModelId_UsesUnknown));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-1", null, 50, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData(model: null, 50, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual("unknown", result.Records[0].Model);
        Assert.AreEqual(50, result.Records[0].InputTokens);
    }

    [TestMethod]
    public void Collect_MalformedTokenField_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MalformedTokenField_CountsBadLine));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-1", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            // Assistant message whose tokens object is missing entirely.
            InsertMessage(db, "m-bad", "s-1", Ms(2026, 7, 20),
                "{\"role\":\"assistant\",\"modelID\":\"m\"}");
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Partial, result.Status.Status);
        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_NegativeToken_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NegativeToken_CountsBadLine));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-1", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-bad", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 100, -5, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void Collect_OutputPlusReasoningOverflow_CountsBadLine()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OutputPlusReasoningOverflow_CountsBadLine));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-1", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-bad", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 0, long.MaxValue, 1, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "s-1");

        Assert.AreEqual(1, result.Status.BadLines);
        Assert.HasCount(1, result.Records);
    }

    [TestMethod]
    public void ResolveDatabasePath_OpenCodeDbAbsolute_IsUsedAsIs()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveDatabasePath_OpenCodeDbAbsolute_IsUsedAsIs));
        var absolute = Path.Combine(workspace.Path, "custom", "db.sqlite");

        var resolved = OpencodeSessionUsageSource.ResolveDatabasePath(
            key => key switch
            {
                "OPENCODE_DB" => absolute,
                _ => null
            },
            () => Path.Combine(workspace.Path, "home"));

        Assert.AreEqual(absolute, resolved);
    }

    [TestMethod]
    public void ResolveDatabasePath_OpenCodeDbRelative_JoinsDataRoot()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveDatabasePath_OpenCodeDbRelative_JoinsDataRoot));
        var xdg = Path.Combine(workspace.Path, "xdg");

        var resolved = OpencodeSessionUsageSource.ResolveDatabasePath(
            key => key switch
            {
                "OPENCODE_DB" => "custom.db",
                "XDG_DATA_HOME" => xdg,
                _ => null
            },
            () => Path.Combine(workspace.Path, "home"));

        Assert.AreEqual(Path.Combine(xdg, "opencode", "custom.db"), resolved);
    }

    [TestMethod]
    public void ResolveDatabasePath_MemoryDatabase_IsReturnedVerbatim()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveDatabasePath_MemoryDatabase_IsReturnedVerbatim));

        var resolved = OpencodeSessionUsageSource.ResolveDatabasePath(
            key => key switch
            {
                "OPENCODE_DB" => ":memory:",
                _ => null
            },
            () => Path.Combine(workspace.Path, "home"));

        Assert.AreEqual(":memory:", resolved);
    }

    [TestMethod]
    public void ResolveDatabasePath_XdgDataHome_RedirectsDataRoot()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveDatabasePath_XdgDataHome_RedirectsDataRoot));
        var xdg = Path.Combine(workspace.Path, "xdg");

        var resolved = OpencodeSessionUsageSource.ResolveDatabasePath(
            key => key == "XDG_DATA_HOME" ? xdg : null,
            () => Path.Combine(workspace.Path, "home"));

        Assert.AreEqual(Path.Combine(xdg, "opencode", "opencode.db"), resolved);
    }

    [TestMethod]
    public void ResolveDatabasePath_NoXdg_FallsBackToLocalShareUnderHome()
    {
        using var workspace = TestWorkspace.Create(nameof(ResolveDatabasePath_NoXdg_FallsBackToLocalShareUnderHome));
        var home = Path.Combine(workspace.Path, "home");

        var resolved = OpencodeSessionUsageSource.ResolveDatabasePath(
            _ => null,
            () => home);

        Assert.AreEqual(
            Path.Combine(home, ".local", "share", "opencode", "opencode.db"),
            resolved);
    }

    [TestMethod]
    public void Collect_MemoryDatabase_IsUnavailableWithUnsupportedFormat()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MemoryDatabase_IsUnavailableWithUnsupportedFormat));

        var result = Collect(new OpencodeSessionUsageSource(() => ":memory:"), "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Unavailable, result.Status.Status);
        Assert.AreEqual(0, result.Status.MatchedSessions);
        Assert.IsEmpty(result.Records);
        AssertReason(result.Status, AgentUsageGapReason.UnsupportedFormat);
    }

    [TestMethod]
    public void Collect_SessionIdWithQuoteAndNewline_IsParameterized()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SessionIdWithQuoteAndNewline_IsParameterized));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        const string hostileId = "psx's\nsession";
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, hostileId, null, 100, 0, 0, 0, 0);
            InsertSession(db, "plain", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-hostile", hostileId, Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
            InsertMessage(db, "m-plain", "plain", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
        }

        var result = Collect(new OpencodeSessionUsageSource(() => dbPath), hostileId);

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
        Assert.AreEqual(1, result.Status.MatchedSessions);
        Assert.HasCount(1, result.Records);
        Assert.AreEqual(0, result.Status.BadLines);
    }

    [TestMethod]
    public void Collect_WalConcurrentWrites_ReadOnlyReadSucceeds()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_WalConcurrentWrites_ReadOnlyReadSucceeds));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var writer = CreateDatabase(dbPath))
        {
            using (var command = writer.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL";
                command.ExecuteNonQuery();
            }

            InsertSession(writer, "psx-wal", null, 100, 0, 0, 0, 0);
            InsertMessage(writer, "wm-1", "psx-wal", Ms(2026, 7, 20),
                AssistantData("wal-model", 100, 0, 0, 0, 0));

            var writerTask = Task.Run(() =>
            {
                for (var index = 0; index < 200; index++)
                {
                    using var command = writer.CreateCommand();
                    command.CommandText = """
                        INSERT INTO message (id, session_id, time_created, data)
                        VALUES ($id, $sessionId, $timeCreated, $data)
                        """;
                    command.Parameters.AddWithValue("$id", "other-" + index);
                    command.Parameters.AddWithValue("$sessionId", "psx-other");
                    command.Parameters.AddWithValue("$timeCreated", Ms(2026, 7, 20));
                    command.Parameters.AddWithValue("$data", AssistantData("wal-model", 10, 0, 0, 0, 0));
                    command.ExecuteNonQuery();
                }
            });

            // Let the writer run for a moment so the read overlaps real writes.
            Thread.Sleep(50);

            var result = Collect(new OpencodeSessionUsageSource(() => dbPath), "psx-wal");

            writerTask.Wait();

            Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);
            Assert.AreEqual(1, result.Status.MatchedSessions);
            Assert.HasCount(1, result.Records);
            Assert.AreEqual(0, result.Status.BadLines);
        }
    }

    [TestMethod]
    public void Collect_ReturnsWithConnectionReleased_DatabaseCanBeDeleted()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ReturnsWithConnectionReleased_DatabaseCanBeDeleted));
        var dbPath = Path.Combine(workspace.Path, "opencode.db");
        using (var db = CreateDatabase(dbPath))
        {
            InsertSession(db, "s-1", null, 100, 0, 0, 0, 0);
            InsertMessage(db, "m-1", "s-1", Ms(2026, 7, 20),
                AssistantData("m", 100, 0, 0, 0, 0));
        }

        var source = new OpencodeSessionUsageSource(() => dbPath);
        var result = Collect(source, "s-1");

        Assert.AreEqual(AgentUsageSourceStatus.Available, result.Status.Status);

        File.Delete(dbPath);
        Assert.IsFalse(File.Exists(dbPath));
    }
}
