using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Exact, PSX-scoped usage source for Kimi Code's durable session wire. The
/// parser is selected by record shape so historical logs survive runtime
/// upgrades; package versions are diagnostic hints, never routing keys.
/// </summary>
public sealed class KimiCodeSessionUsageSource : IAgentUsageSource
{
    public const string SourceKey = "acp-kimi";
    public const string ParserVersion = "kimi-wire-usage-record/v1";

    private static readonly HashSet<string> ActivityRecordTypes = new(StringComparer.Ordinal)
    {
        "llm.request",
        "turn.prompt",
        "context.append_loop_event"
    };

    private readonly Func<string?> _homeResolver;

    public KimiCodeSessionUsageSource(Func<string?>? homeResolver = null)
    {
        _homeResolver = homeResolver ?? DefaultHome;
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

        var home = _homeResolver();
        var sessionsRoot = string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, "sessions");
        if (sessionsRoot == null || !Directory.Exists(sessionsRoot))
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                Detail: "Kimi session directory was not found.")
            {
                Reasons = [AgentUsageGapReason.MissingSessionLogs]
            };
        }

        var pathsBySession = wanted.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        var probeableSessionIds = wanted
            .Where(IsSafeSessionId)
            .ToArray();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var counters = new ScanCounters();

        try
        {
            foreach (var workDir in Directory.EnumerateDirectories(sessionsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var sessionId in probeableSessionIds)
                {
                    var sessionDir = Path.Combine(workDir, sessionId);
                    if (TryDirectoryExists(sessionDir, out var unreadable))
                    {
                        pathsBySession[sessionId].Add(sessionDir);
                    }
                    else if (unreadable)
                    {
                        counters.TraversalFailed = true;
                        reasons.Add(AgentUsageGapReason.UnreadableLogs);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.TraversalFailed = true;
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
        }

        var matched = 0;
        foreach (var sessionId in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = pathsBySession[sessionId];
            if (matches.Count == 0)
            {
                reasons.Add(AgentUsageGapReason.MissingSessionLogs);
                continue;
            }

            if (matches.Count > 1)
            {
                reasons.Add(AgentUsageGapReason.AmbiguousSessionLogs);
                continue;
            }

            var result = ParseSession(
                matches[0], sink, counters, cancellationToken);
            if (result.Matched)
                matched++;
            foreach (var reason in result.Reasons)
                reasons.Add(reason);
        }

        if (matched < expected)
            reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        if (counters.TraversalFailed || counters.SkippedFiles > 0 || counters.BadLines > 0)
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

    private static SessionParseResult ParseSession(
        string sessionDir,
        IAgentUsageRecordSink sink,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var agentsRoot = Path.Combine(sessionDir, "agents");
        var mainWire = Path.Combine(agentsRoot, "main", "wire.jsonl");
        if (!File.Exists(mainWire))
        {
            reasons.Add(AgentUsageGapReason.MissingSessionLogs);
            return new SessionParseResult(false, OrderReasons(reasons));
        }

        string[] agentDirectories;
        try
        {
            agentDirectories = Directory.EnumerateDirectories(agentsRoot).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
            counters.TraversalFailed = true;
            return new SessionParseResult(false, OrderReasons(reasons));
        }

        var wireStates = new List<WireState>();
        foreach (var agentDir in agentDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wirePath = Path.Combine(agentDir, "wire.jsonl");
            if (!File.Exists(wirePath))
            {
                counters.SkippedFiles++;
                reasons.Add(AgentUsageGapReason.UnreadableLogs);
                continue;
            }

            var state = new WireState();
            wireStates.Add(state);
            counters.ScannedFiles++;
            try
            {
                var oversized = BoundedJsonlReader.Read(
                    wirePath,
                    line => ParseWireLine(
                        line,
                        sink,
                        state,
                        counters),
                    cancellationToken);
                if (oversized > 0)
                {
                    state.Unreadable = true;
                    counters.BadLines = checked(counters.BadLines + oversized);
                    reasons.Add(AgentUsageGapReason.UnreadableLogs);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.Unreadable = true;
                counters.SkippedFiles++;
                reasons.Add(AgentUsageGapReason.UnreadableLogs);
            }
        }

        var hasRecord = wireStates.Any(state => state.HasRecord);
        var hasActivity = wireStates.Any(state => state.HasActivity);
        var allReadable = wireStates.Count > 0
            && wireStates.Count == agentDirectories.Length
            && wireStates.All(state => !state.Unreadable);
        var recognizedEmpty = allReadable
            && wireStates.All(state => state.MetadataRecognized)
            && !hasRecord
            && !hasActivity;

        if (!hasRecord && !recognizedEmpty)
            reasons.Add(AgentUsageGapReason.UnsupportedFormat);

        return new SessionParseResult(
            allReadable && (hasRecord || recognizedEmpty),
            OrderReasons(reasons));
    }

    private static void ParseWireLine(
        ReadOnlyMemory<byte> line,
        IAgentUsageRecordSink sink,
        WireState state,
        ScanCounters counters)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            state.Unreadable = true;
            counters.BadLines++;
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeProperty.GetString();
            if (type == "metadata")
            {
                state.MetadataRecognized =
                    root.TryGetProperty("protocol_version", out var protocol)
                    && protocol.ValueKind == JsonValueKind.String
                    && protocol.GetString() is { } value
                    && value.StartsWith("1.", StringComparison.Ordinal);
                return;
            }

            if (type == "usage.record")
            {
                state.HasActivity = true;
                if (!TryParseUsageRecord(root, out var record))
                {
                    state.Unreadable = true;
                    counters.BadLines++;
                    return;
                }

                state.HasRecord = true;
                sink.Add(record!);
                return;
            }

            if (type != null && ActivityRecordTypes.Contains(type))
                state.HasActivity = true;
        }
    }

    private static bool TryParseUsageRecord(
        JsonElement root,
        out AgentUsageRecord? record)
    {
        record = null;
        if (!root.TryGetProperty("time", out var time)
            || time.ValueKind != JsonValueKind.Number
            || !time.TryGetInt64(out var unixMilliseconds)
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !TryGetToken(usage, "inputOther", out var input)
            || !TryGetToken(usage, "output", out var output)
            || !TryGetToken(usage, "inputCacheRead", out var cacheRead)
            || !TryGetToken(usage, "inputCacheCreation", out var cacheCreation))
        {
            return false;
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var model = root.TryGetProperty("model", out var modelProperty)
            && modelProperty.ValueKind == JsonValueKind.String
            ? modelProperty.GetString() ?? "unknown"
            : "unknown";
        record = new AgentUsageRecord(
            timestamp, model, input, output, cacheRead, cacheCreation);
        return true;
    }

    private static bool TryGetToken(
        JsonElement usage,
        string propertyName,
        out long value)
    {
        value = 0;
        return usage.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 0;
    }

    private static string? DefaultHome()
    {
        var overridden = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".kimi-code");
    }

    private static bool TryDirectoryExists(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return false;
        }
    }

    private static bool IsSafeSessionId(string sessionId) =>
        sessionId.Length > 0
        && sessionId != "."
        && sessionId != ".."
        && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !sessionId.Contains(Path.DirectorySeparatorChar)
        && !sessionId.Contains(Path.AltDirectorySeparatorChar);

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
        public bool TraversalFailed { get; set; }
    }

    private sealed class WireState
    {
        public bool MetadataRecognized { get; set; }
        public bool HasActivity { get; set; }
        public bool HasRecord { get; set; }
        public bool Unreadable { get; set; }
    }

    private sealed record SessionParseResult(
        bool Matched,
        IReadOnlyList<string> Reasons);
}
