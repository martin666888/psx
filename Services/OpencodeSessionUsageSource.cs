using System.IO;
using Microsoft.Data.Sqlite;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Exact, PSX-scoped usage source for OpenCode's SQLite session database
/// (verified against the official v1.18.14 schema and JSON1 projections).
/// The read-only <c>opencode.db</c> holds per-session token aggregates on
/// <c>session</c> and per-assistant-message usage on <c>message</c>; both are
/// cross-checked so a schema or accounting drift is reported instead of being
/// silently trusted.
/// </summary>
public sealed class OpencodeSessionUsageSource : IAgentUsageSource
{
    public const string SourceKey = "acp-opencode";
    public const string ParserVersion = "opencode-sqlite-usage/v1";

    private const string UnknownModel = "unknown";
    private const int MaxParentDepth = 32;
    private const int MaxInBatchSize = 400;
    private const int BusyTimeoutMilliseconds = 500;

    private static readonly string[] RequiredSessionColumns =
    [
        "id",
        "parent_id",
        "tokens_input",
        "tokens_output",
        "tokens_reasoning",
        "tokens_cache_read",
        "tokens_cache_write"
    ];

    private static readonly string[] RequiredMessageColumns =
    [
        "id",
        "session_id",
        "time_created",
        "data"
    ];

    private readonly Func<string?> _databasePathResolver;

    public OpencodeSessionUsageSource(Func<string?>? databasePathResolver = null)
    {
        _databasePathResolver = databasePathResolver ?? DefaultDatabasePath;
    }

    public string? ResolveSessionId(AgentUsageThreadSnapshot thread) =>
        thread.AcpSessionId;

    public AgentUsageSourceStatus Collect(
        IReadOnlyCollection<string> sessionIds,
        IAgentUsageRecordSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var now = DateTimeOffset.Now;
        var wanted = new HashSet<string>(
            sessionIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var expected = wanted.Count;
        if (expected == 0)
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Available, 0, 0, 0,
                ExpectedSessions: 0, MatchedSessions: 0, ParserVersion, now, Detail: null);
        }

        var databasePath = _databasePathResolver();
        if (string.Equals(databasePath, ":memory:", StringComparison.Ordinal))
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                Detail: "OpenCode :memory: databases cannot be read by PSX.")
            {
                Reasons = [AgentUsageGapReason.UnsupportedFormat]
            };
        }

        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                Detail: "OpenCode session database was not found.")
            {
                Reasons = [AgentUsageGapReason.MissingSessionLogs]
            };
        }

        var counters = new ScanCounters();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var gapSessions = new HashSet<string>(StringComparer.Ordinal);
        var matched = 0;

        try
        {
            using var connection = OpenReadOnlyConnection(databasePath);
            using var transaction = connection.BeginTransaction();

            if (!SchemaSupported(connection))
            {
                return new AgentUsageSourceStatus(
                    SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                    ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                    Detail: "OpenCode database schema is not the expected v1.18.14 shape.")
                {
                    Reasons = [AgentUsageGapReason.UnsupportedFormat]
                };
            }

            counters.ScannedFiles = 1;
            var expandedIds = ExpandSessionSet(connection, wanted, counters, cancellationToken);
            matched = counters.RootsFound;

            var sessionSums = LoadSessionTokenSums(
                connection, expandedIds, gapSessions, cancellationToken);
            var messageSums = LoadMessageRecords(
                connection, expandedIds, sink, counters, gapSessions, cancellationToken);

            foreach (var sessionId in expandedIds)
            {
                if (gapSessions.Contains(sessionId))
                {
                    counters.CrossCheckGaps++;
                    continue;
                }

                var sessionSum = sessionSums.TryGetValue(sessionId, out var sessionTotal)
                    ? sessionTotal
                    : 0;
                var messageSum = messageSums.TryGetValue(sessionId, out var messageTotal)
                    ? messageTotal
                    : 0;
                if (sessionSum != messageSum)
                    counters.CrossCheckGaps++;
            }
        }
        catch (SqliteException)
        {
            counters.SkippedFiles++;
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.SkippedFiles++;
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
        }

        if (matched < expected)
            reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        if (counters.CrossCheckGaps > 0)
            reasons.Add(AgentUsageGapReason.UnsupportedFormat);
        if (counters.SkippedFiles > 0 || counters.BadLines > 0)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);

        var status = matched == 0
            ? AgentUsageSourceStatus.Unavailable
            : reasons.Count > 0
                ? AgentUsageSourceStatus.Partial
                : AgentUsageSourceStatus.Available;

        return new AgentUsageSourceStatus(
            SourceKey,
            status,
            counters.ScannedFiles,
            counters.SkippedFiles,
            counters.BadLines,
            ExpectedSessions: expected,
            MatchedSessions: matched,
            ParserVersion,
            now,
            Detail: reasons.Count == 0 ? null : $"matched {matched} of {expected} sessions")
        {
            Reasons = OrderReasons(reasons)
        };
    }

    // ---- database path resolution ----

    /// <summary>
    /// Mirrors the official <c>Database.path()</c>: <c>OPENCODE_DB</c> wins
    /// (<c>:memory:</c> is returned verbatim so the caller can reject it;
    /// absolute values are used as-is, relative values join the data root),
    /// otherwise <c>&lt;data root&gt;/opencode.db</c> where the data root is
    /// <c>XDG_DATA_HOME/opencode</c> with a Windows fallback of
    /// <c>~/.local/share/opencode</c>. Channel-specific
    /// <c>opencode-&lt;channel&gt;.db</c> names are never probed because PSX
    /// only ever manages the release-channel runtime.
    /// </summary>
    internal static string? ResolveDatabasePath(
        Func<string, string?> environment,
        Func<string?> homeResolver)
    {
        var overrideDb = environment("OPENCODE_DB");
        if (string.Equals(overrideDb, ":memory:", StringComparison.Ordinal))
            return ":memory:";

        var xdg = environment("XDG_DATA_HOME");
        var home = homeResolver();
        string? dataRoot;
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            dataRoot = Path.Combine(xdg, "opencode");
        }
        else if (!string.IsNullOrWhiteSpace(home))
        {
            dataRoot = Path.Combine(home, ".local", "share", "opencode");
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(overrideDb))
            return Path.Combine(dataRoot, "opencode.db");
        return Path.IsPathRooted(overrideDb)
            ? overrideDb
            : Path.Combine(dataRoot, overrideDb);
    }

    private static string? DefaultDatabasePath() =>
        ResolveDatabasePath(Environment.GetEnvironmentVariable, DefaultHome);

    private static string? DefaultHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    // ---- read helpers ----

    private static SqliteConnection OpenReadOnlyConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            // Short busy timeout: never block the UI thread for long on a
            // database that is mid-write (the official client uses 5000 ms).
            command.CommandText = "PRAGMA busy_timeout = " + BusyTimeoutMilliseconds;
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static bool SchemaSupported(SqliteConnection connection) =>
        HasColumns(connection, "session", RequiredSessionColumns)
        && HasColumns(connection, "message", RequiredMessageColumns);

    private static bool HasColumns(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<string> requiredColumns)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + tableName + ")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            present.Add(reader.GetString(1));
        return requiredColumns.All(present.Contains);
    }

    private static HashSet<string> ExpandSessionSet(
        SqliteConnection connection,
        HashSet<string> wanted,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Id, int Depth)>();
        foreach (var root in wanted)
        {
            if (!SessionExists(connection, root))
                continue;
            counters.RootsFound++;
            if (expanded.Add(root))
                pending.Enqueue((root, 0));
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parentId, depth) = pending.Dequeue();
            if (depth >= MaxParentDepth)
                continue;
            foreach (var childId in QueryChildren(connection, parentId))
            {
                if (expanded.Add(childId))
                    pending.Enqueue((childId, depth + 1));
            }
        }

        return expanded;
    }

    private static bool SessionExists(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM session WHERE id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", sessionId);
        return command.ExecuteScalar() != null;
    }

    private static List<string> QueryChildren(SqliteConnection connection, string parentId)
    {
        var children = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM session WHERE parent_id = $parent";
        command.Parameters.AddWithValue("$parent", parentId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                children.Add(reader.GetString(0));
        }

        return children;
    }

    private static Dictionary<string, long> LoadSessionTokenSums(
        SqliteConnection connection,
        HashSet<string> sessionIds,
        HashSet<string> gapSessions,
        CancellationToken cancellationToken)
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var batch in Batch(sessionIds, MaxInBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, tokens_input, tokens_output, tokens_reasoning,"
                + " tokens_cache_read, tokens_cache_write"
                + " FROM session WHERE id IN (" + InPlaceholders(batch) + ")";
            AddInParameters(command, batch);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    continue;
                var sessionId = reader.GetString(0);
                long sum = 0;
                for (var ordinal = 1; ordinal <= 5; ordinal++)
                {
                    long value;
                    if (reader.IsDBNull(ordinal))
                    {
                        value = 0;
                    }
                    else
                    {
                        try
                        {
                            value = reader.GetInt64(ordinal);
                        }
                        catch (Exception ex) when (
                            ex is InvalidCastException or FormatException or OverflowException)
                        {
                            gapSessions.Add(sessionId);
                            value = 0;
                        }
                    }

                    try
                    {
                        sum = checked(sum + value);
                    }
                    catch (OverflowException)
                    {
                        gapSessions.Add(sessionId);
                        sum = 0;
                        break;
                    }
                }

                sums[sessionId] = sum;
            }
        }

        return sums;
    }

    private static Dictionary<string, long> LoadMessageRecords(
        SqliteConnection connection,
        HashSet<string> sessionIds,
        IAgentUsageRecordSink sink,
        ScanCounters counters,
        HashSet<string> gapSessions,
        CancellationToken cancellationToken)
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
            sums[sessionId] = 0;

        foreach (var batch in Batch(sessionIds, MaxInBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.BadLines = checked(
                counters.BadLines + CountInvalidJsonRows(connection, batch));

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT session_id, time_created,"
                + " json_extract(data, '$.modelID'),"
                + " json_extract(data, '$.tokens.input'),"
                + " json_extract(data, '$.tokens.output'),"
                + " json_extract(data, '$.tokens.reasoning'),"
                + " json_extract(data, '$.tokens.cache.read'),"
                + " json_extract(data, '$.tokens.cache.write')"
                + " FROM message WHERE session_id IN (" + InPlaceholders(batch) + ")"
                + " AND json_valid(data) = 1"
                + " AND json_extract(data, '$.role') = 'assistant'";
            AddInParameters(command, batch);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseMessageRow(reader, out var sessionId, out var record, out var rawSum))
                {
                    counters.BadLines++;
                    continue;
                }

                sink.Add(record!);
                try
                {
                    sums[sessionId] = checked(sums[sessionId] + rawSum);
                }
                catch (OverflowException)
                {
                    gapSessions.Add(sessionId);
                }
            }
        }

        return sums;
    }

    private static int CountInvalidJsonRows(SqliteConnection connection, string[] batch)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM message"
            + " WHERE session_id IN (" + InPlaceholders(batch) + ")"
            + " AND json_valid(data) = 0";
        AddInParameters(command, batch);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryParseMessageRow(
        SqliteDataReader reader,
        out string sessionId,
        out AgentUsageRecord? record,
        out long rawSum)
    {
        sessionId = string.Empty;
        record = null;
        rawSum = 0;

        if (reader.IsDBNull(0))
            return false;
        sessionId = reader.GetString(0);

        if (reader.IsDBNull(1))
            return false;
        long timeMilliseconds;
        try
        {
            timeMilliseconds = reader.GetInt64(1);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        string model;
        if (reader.IsDBNull(2))
        {
            model = UnknownModel;
        }
        else
        {
            try
            {
                model = reader.GetString(2);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException)
            {
                return false;
            }
        }

        if (!TryReadToken(reader, 3, out var input)
            || !TryReadToken(reader, 4, out var output)
            || !TryReadToken(reader, 5, out var reasoning)
            || !TryReadToken(reader, 6, out var cacheRead)
            || !TryReadToken(reader, 7, out var cacheWrite))
        {
            return false;
        }

        long outputTokens;
        long messageSum;
        try
        {
            outputTokens = checked(output + reasoning);
            messageSum = checked(input + output + reasoning + cacheRead + cacheWrite);
        }
        catch (OverflowException)
        {
            return false;
        }

        record = new AgentUsageRecord(
            timestamp, model, input, outputTokens, cacheRead, cacheWrite);
        rawSum = messageSum;
        return true;
    }

    private static bool TryReadToken(SqliteDataReader reader, int ordinal, out long value)
    {
        value = 0;
        if (reader.IsDBNull(ordinal))
            return false;
        try
        {
            value = reader.GetInt64(ordinal);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }

        return value >= 0;
    }

    private static string InPlaceholders(IReadOnlyList<string> batch) =>
        string.Join(", ", batch.Select((_, index) => "$id" + index));

    private static void AddInParameters(SqliteCommand command, IReadOnlyList<string> batch)
    {
        for (var index = 0; index < batch.Count; index++)
            command.Parameters.AddWithValue("$id" + index, batch[index]);
    }

    private static IEnumerable<string[]> Batch(IEnumerable<string> ids, int batchSize)
    {
        var current = new List<string>(batchSize);
        foreach (var id in ids)
        {
            current.Add(id);
            if (current.Count == batchSize)
            {
                yield return current.ToArray();
                current = new List<string>(batchSize);
            }
        }

        if (current.Count > 0)
            yield return current.ToArray();
    }

    private static IReadOnlyList<string> OrderReasons(IEnumerable<string> reasons)
    {
        var set = new HashSet<string>(reasons, StringComparer.Ordinal);
        return AgentUsageGapReason.Ordered.Where(set.Contains).ToArray();
    }

    private sealed class ScanCounters
    {
        public int ScannedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int BadLines { get; set; }
        public int RootsFound { get; set; }
        public int CrossCheckGaps { get; set; }
    }
}
