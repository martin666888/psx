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

    public AgentUsageContribution Collect(
        IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken)
    {
        var expected = sessionIds.Count;
        var now = DateTimeOffset.Now;

        // No PSX Claude sessions: available with zero data, regardless of
        // whether the Claude directory exists.
        if (expected == 0)
        {
            return new AgentUsageContribution(
                [],
                new AgentUsageSourceStatus(
                    SourceKey, AgentUsageSourceStatus.Available, 0, 0, 0,
                    ExpectedSessions: 0, MatchedSessions: 0, ParserVersion, now, Detail: null));
        }

        var configDir = _configDirResolver();
        var projectsDir = string.IsNullOrWhiteSpace(configDir)
            ? null
            : Path.Combine(configDir, "projects");

        if (projectsDir == null || !Directory.Exists(projectsDir))
        {
            return new AgentUsageContribution(
                [],
                new AgentUsageSourceStatus(
                    SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                    ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                    Detail: "Claude session directory was not found. Usage cannot be read for these sessions."));
        }

        var wanted = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
        var records = new List<AgentUsageRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matchedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedFiles = 0;
        var skippedFiles = 0;
        var badLines = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AgentUsageContribution(
                [],
                new AgentUsageSourceStatus(
                    SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                    ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now, Detail: ex.Message));
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The file name stem is the session id; skip files PSX did not own.
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!wanted.Contains(stem))
                continue;

            scannedFiles++;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skippedFiles++;
                continue;
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (!TryParseLine(line, stem, seen, out var record))
                {
                    if (record == null && IsBadLine(line))
                        badLines++;
                    continue;
                }

                matchedSessions.Add(stem);
                records.Add(record!);
            }
        }

        var status = DetermineStatus(expected, matchedSessions.Count, scannedFiles, skippedFiles, badLines, now);
        return new AgentUsageContribution(records, status);
    }

    private static bool IsBadLine(string line)
    {
        try
        {
            using var _ = JsonDocument.Parse(line);
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static AgentUsageSourceStatus DetermineStatus(
        int expected, int matched, int scannedFiles, int skippedFiles, int badLines, DateTimeOffset now)
    {
        var partial = matched < expected || skippedFiles > 0 || badLines > 0;
        var status = partial ? AgentUsageSourceStatus.Partial : AgentUsageSourceStatus.Available;
        string? detail = null;
        if (partial)
        {
            var parts = new List<string>();
            if (matched < expected)
                parts.Add($"matched {matched} of {expected} sessions");
            if (skippedFiles > 0)
                parts.Add($"skipped {skippedFiles} unreadable file(s)");
            if (badLines > 0)
                parts.Add($"{badLines} malformed line(s)");
            detail = "Partial data: " + string.Join(", ", parts) + ".";
        }

        return new AgentUsageSourceStatus(
            SourceKey, status, scannedFiles, skippedFiles, badLines,
            ExpectedSessions: expected, MatchedSessions: matched, ParserVersion, now, detail);
    }

    private static bool TryParseLine(
        string line, string sessionId, HashSet<string> seen, out AgentUsageRecord? record)
    {
        record = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != "assistant")
            {
                return false;
            }

            if (!root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!message.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Dedup by (sessionId, messageId): streamed retries or duplicate
            // files must not double-count the same assistant turn.
            var messageId = message.TryGetProperty("id", out var idProp)
                && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            if (!string.IsNullOrEmpty(messageId) && !seen.Add(sessionId + "|" + messageId))
                return false;

            var model = message.TryGetProperty("model", out var modelProp)
                && modelProp.ValueKind == JsonValueKind.String
                ? modelProp.GetString() ?? "unknown"
                : "unknown";

            var timestamp = root.TryGetProperty("timestamp", out var tsProp)
                && tsProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsProp.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            record = new AgentUsageRecord(
                timestamp,
                model,
                GetLong(usage, "input_tokens"),
                GetLong(usage, "output_tokens"),
                GetLong(usage, "cache_read_input_tokens"),
                GetLong(usage, "cache_creation_input_tokens"));
            return true;
        }
    }

    private static long GetLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;
}
