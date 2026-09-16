using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>Pi v3 JSONL usage, restricted to PSX session ids, including inactive branches.</summary>
public sealed class PiSessionUsageSource(Func<string?>? directoryResolver = null) : IAgentUsageSource
{
    private const int MaximumIds = 250000;

    public AgentUsageSourceStatus Collect(IReadOnlyCollection<string> sessionIds, IAgentUsageRecordSink sink, CancellationToken cancellationToken)
    {
        var wanted = sessionIds.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new HashSet<string>();
        var scanned = 0;
        var skipped = 0;
        var bad = 0;
        var records = 0;
        var root = (directoryResolver ?? (() => PiConfigSource.AgentDirectory() is { } home ? Path.Combine(home, "sessions") : null))();
        try
        {
            if (wanted.Count > 0 && root != null && Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(path);
                    var separator = name.LastIndexOf('_');
                    if (separator < 0 || !wanted.Contains(name[(separator + 1)..])) continue;
                    var id = name[(separator + 1)..];
                    found.Add(id);
                    scanned++;
                    var valid = true;
                    var headerSeen = false;
                    var lineageResolved = true;
                    HashSet<string>? inherited = null;
                    try
                    {
                        var oversized = BoundedJsonlReader.Read(path, line =>
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                var entry = doc.RootElement;
                                var type = entry.GetProperty("type").GetString();
                                if (!headerSeen)
                                {
                                    if (type != "session" || entry.GetProperty("id").GetString() != id
                                        || entry.GetProperty("version").GetInt32() != 3) throw new JsonException();
                                    headerSeen = true;
                                    if (entry.TryGetProperty("parentSession", out var parent) && parent.ValueKind == JsonValueKind.String)
                                    {
                                        inherited = ReadInheritedIds(parent.GetString()!, id, cancellationToken);
                                        if (inherited == null) { valid = false; lineageResolved = false; reasons.Add(AgentUsageGapReason.LineageUnresolved); }
                                    }
                                    return;
                                }
                                if (!lineageResolved) return;
                                if (type == "session") throw new JsonException();
                                if (type is not ("message" or "compaction" or "branch_summary")) return;
                                var payload = type == "message" ? entry.GetProperty("message") : entry;
                                if (type == "message" && payload.GetProperty("role").GetString() != "assistant") return;
                                var entryId = entry.GetProperty("id").GetString();
                                if (string.IsNullOrWhiteSpace(entryId)) throw new JsonException();
                                if (inherited?.Contains(EntryIdentity(entry)) == true) return;
                                if (!payload.TryGetProperty("usage", out var usage))
                                { valid = false; reasons.Add(AgentUsageGapReason.UnsupportedFormat); return; }
                                var timestamp = DateTimeOffset.Parse(entry.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture);
                                var input = Tokens(usage, "input");
                                var output = Tokens(usage, "output");
                                var cacheRead = Tokens(usage, "cacheRead");
                                var cacheWrite = Tokens(usage, "cacheWrite");
                                var total = checked(input + output + cacheRead + cacheWrite);
                                if (Tokens(usage, "totalTokens") != total) throw new JsonException();
                                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                                    $"{id}\n{entryId}\n{timestamp:O}\n{input}\n{output}\n{cacheRead}\n{cacheWrite}")));
                                if (seen.Count >= MaximumIds) { valid = false; reasons.Add(AgentUsageGapReason.DiscoveryTruncated); return; }
                                if (!seen.Add(hash)) return;
                                sink.Add(new(timestamp, "", input, output, cacheRead, cacheWrite));
                                records++;
                            }
                            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
                            { valid = false; bad++; }
                        }, cancellationToken);
                        bad += oversized;
                        if (oversized > 0) valid = false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { valid = false; skipped++; }
                    if (valid && headerSeen) matched.Add(id);
                    else incomplete.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { skipped++; }
        matched.ExceptWith(incomplete);
        if (found.Count < wanted.Count) reasons.Add(AgentUsageGapReason.MissingSessionLogs);
        if (matched.Count < wanted.Count) reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        if (bad > 0 || skipped > 0) reasons.Add(AgentUsageGapReason.UnreadableLogs);
        var status = reasons.Count == 0 ? AgentUsageSourceStatus.Available
            : records > 0 || matched.Count > 0 ? AgentUsageSourceStatus.Partial : AgentUsageSourceStatus.Unavailable;
        return new("acp-pi", status, scanned, skipped, bad, wanted.Count, matched.Count, "1", DateTimeOffset.Now, null)
        { Reasons = AgentUsageGapReason.Ordered.Where(reasons.Contains).ToArray() };
    }

    private static long Tokens(JsonElement usage, string name)
    {
        var value = usage.GetProperty(name).GetInt64();
        return value >= 0 ? value : throw new JsonException();
    }

    private static string EntryIdentity(JsonElement entry)
    {
        var id = entry.GetProperty("id").GetString();
        var timestamp = entry.GetProperty("timestamp").GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(timestamp)) throw new JsonException();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + timestamp)));
    }

    private static HashSet<string>? ReadInheritedIds(string path, string childId, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var valid = true;
        var headerSeen = false;
        try
        {
            var oversized = BoundedJsonlReader.Read(path, line =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var entry = doc.RootElement;
                    if (!headerSeen)
                    {
                        if (entry.GetProperty("type").GetString() != "session" || entry.GetProperty("version").GetInt32() != 3
                            || string.IsNullOrWhiteSpace(entry.GetProperty("id").GetString()) || entry.GetProperty("id").GetString() == childId)
                            throw new JsonException();
                        headerSeen = true;
                        return;
                    }
                    if (entry.GetProperty("type").GetString() == "session") throw new JsonException();
                    if (ids.Count >= MaximumIds) valid = false;
                    else ids.Add(EntryIdentity(entry));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { valid = false; }
            }, cancellationToken);
            return valid && headerSeen && oversized == 0 ? ids : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
