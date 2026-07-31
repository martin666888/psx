using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// PSX-only exact-usage source for Claude Code. Scans the Claude session log
/// tree (<c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>, under <c>projects/</c>)
/// but only counts files whose session id belongs to a PSX thread. The
/// <c>projects/**/*.jsonl</c> layout has no official on-disk contract, so this
/// is a best-effort parser: malformed lines are counted, never fatal, and the
/// completeness surfaces in the reported <see cref="AgentUsageSourceStatus"/>.
/// </summary>
public sealed class ClaudeSessionUsageSource : IAgentUsageSource
{
    public const string SourceKey = "acp-claude";
    public const string ParserVersion = "1";

    private readonly Func<string?> _configDirResolver;

    public ClaudeSessionUsageSource(Func<string?>? configDirResolver = null)
    {
        _configDirResolver = configDirResolver ?? DefaultConfigDir;
    }

    private static string? DefaultConfigDir()
    {
        var overridden = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude");
    }

    public AgentUsageSourceStatus Collect(
        IReadOnlyCollection<string> sessionIds,
        IAgentUsageRecordSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var expected = sessionIds.Count;
        var now = DateTimeOffset.Now;

        if (expected == 0)
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Available, 0, 0, 0,
                ExpectedSessions: 0, MatchedSessions: 0, ParserVersion, now, Detail: null);
        }

        var configDir = _configDirResolver();
        var projectsDir = string.IsNullOrWhiteSpace(configDir)
            ? null
            : Path.Combine(configDir, "projects");

        if (projectsDir == null || !Directory.Exists(projectsDir))
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                Detail: "Claude session directory was not found. Usage cannot be read for these sessions.")
            {
                Reasons = [AgentUsageGapReason.MissingSessionLogs]
            };
        }

        var wanted = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var states = wanted.ToDictionary(
            id => id,
            _ => new SessionState(),
            StringComparer.OrdinalIgnoreCase);
        var probeableSessionIds = wanted
            .Where(IsSafeSessionId)
            .ToArray();
        var scannedFiles = 0;
        var skippedFiles = 0;
        var badLines = 0;
        var traversalFailed = false;
        try
        {
            var projectDirectories = Directory
                .EnumerateDirectories(projectsDir, "*", SearchOption.AllDirectories)
                .Prepend(projectsDir);
            foreach (var projectDirectory in projectDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var sessionId in probeableSessionIds)
                {
                    var file = Path.Combine(projectDirectory, sessionId + ".jsonl");
                    if (!TryFileExists(file, out var unreadable))
                    {
                        if (unreadable)
                        {
                            states[sessionId].Unreadable = true;
                            traversalFailed = true;
                        }
                        continue;
                    }

                    var state = states[sessionId];
                    scannedFiles++;
                    state.FoundFile = true;
                    try
                    {
                        var oversized = BoundedJsonlReader.Read(
                            file,
                            line =>
                            {
                                var outcome = ParseLine(line, sessionId, seen, out var record);
                                switch (outcome)
                                {
                                    case ParseOutcome.Record:
                                        state.HasActivity = true;
                                        state.HasRecord = true;
                                        sink.Add(record!);
                                        break;
                                    case ParseOutcome.Activity:
                                        state.HasActivity = true;
                                        break;
                                    case ParseOutcome.Invalid:
                                        state.Unreadable = true;
                                        badLines++;
                                        break;
                                }
                            },
                            cancellationToken);
                        if (oversized > 0)
                        {
                            state.Unreadable = true;
                            badLines = checked(badLines + oversized);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        state.Unreadable = true;
                        skippedFiles++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            traversalFailed = true;
        }

        var matched = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states.Values)
        {
            if ((state.HasRecord && !state.Unreadable)
                || (state.FoundFile && !state.HasActivity && !state.Unreadable))
            {
                matched++;
                continue;
            }

            if (!state.FoundFile)
                reasons.Add(AgentUsageGapReason.MissingSessionLogs);
            else if (state.HasActivity && !state.HasRecord)
                reasons.Add(AgentUsageGapReason.UnsupportedFormat);
        }

        if (matched < expected)
            reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        if (traversalFailed || skippedFiles > 0 || badLines > 0)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);

        return BuildStatus(
            expected, matched, scannedFiles, skippedFiles, badLines, now, reasons);
    }

    private static AgentUsageSourceStatus BuildStatus(
        int expected,
        int matched,
        int scannedFiles,
        int skippedFiles,
        int badLines,
        DateTimeOffset now,
        IReadOnlySet<string> reasons)
    {
        var hasGap = reasons.Count > 0;
        var status = expected > 0 && matched == 0
            ? AgentUsageSourceStatus.Unavailable
            : hasGap
                ? AgentUsageSourceStatus.Partial
                : AgentUsageSourceStatus.Available;

        return new AgentUsageSourceStatus(
            SourceKey, status, scannedFiles, skippedFiles, badLines,
            ExpectedSessions: expected, MatchedSessions: matched, ParserVersion, now,
            Detail: hasGap ? $"matched {matched} of {expected} sessions" : null)
        {
            Reasons = OrderReasons(reasons)
        };
    }

    private static ParseOutcome ParseLine(
        ReadOnlyMemory<byte> line,
        string sessionId,
        HashSet<string> seen,
        out AgentUsageRecord? record)
    {
        record = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return ParseOutcome.Invalid;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ParseOutcome.Ignored;
            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                return ParseOutcome.Ignored;
            }

            var type = typeProp.GetString();
            if (type != "assistant")
                return type == "user" ? ParseOutcome.Activity : ParseOutcome.Ignored;

            if (!root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object)
            {
                return ParseOutcome.Activity;
            }

            if (!message.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return ParseOutcome.Activity;
            }

            // Dedup by (sessionId, messageId): streamed retries or duplicate
            // files must not double-count the same assistant turn.
            var messageId = message.TryGetProperty("id", out var idProp)
                && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            if (!string.IsNullOrEmpty(messageId) && !seen.Add(sessionId + "|" + messageId))
                return ParseOutcome.Ignored;

            var model = message.TryGetProperty("model", out var modelProp)
                && modelProp.ValueKind == JsonValueKind.String
                ? modelProp.GetString() ?? "unknown"
                : "unknown";

            var timestamp = root.TryGetProperty("timestamp", out var tsProp)
                && tsProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsProp.GetString(), out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            if (!timestamp.HasValue
                || !TryGetToken(usage, "input_tokens", required: true, out var input)
                || !TryGetToken(usage, "output_tokens", required: true, out var output)
                || !TryGetToken(usage, "cache_read_input_tokens", required: false, out var cacheRead)
                || !TryGetToken(usage, "cache_creation_input_tokens", required: false, out var cacheCreation))
            {
                return ParseOutcome.Invalid;
            }

            record = new AgentUsageRecord(
                timestamp.Value,
                model,
                input,
                output,
                cacheRead,
                cacheCreation);
            return ParseOutcome.Record;
        }
    }

    private static bool TryGetToken(
        JsonElement element,
        string propertyName,
        bool required,
        out long number)
    {
        number = 0;
        if (!element.TryGetProperty(propertyName, out var value))
            return !required;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number)
            && number >= 0;
    }

    private static bool TryFileExists(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
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

    private sealed class SessionState
    {
        public bool FoundFile { get; set; }
        public bool HasActivity { get; set; }
        public bool HasRecord { get; set; }
        public bool Unreadable { get; set; }
    }

    private enum ParseOutcome
    {
        Ignored,
        Activity,
        Record,
        Invalid
    }
}
