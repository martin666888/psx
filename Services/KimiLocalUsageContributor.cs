using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

internal enum KimiWireScanMode
{
    CacheHit,
    Append,
    Full
}

/// <summary>
/// Local-all usage contributor for Kimi Code: counts every session found under
/// <c>KIMI_CODE_HOME ?? ~/.kimi-code</c> (ACP, Web and CLI/TUI alike),
/// independently of the ThreadStore, and claims the acp-kimi provider's
/// threads. Per-file incremental state lives in a disposable disk cache keyed
/// by path hash; cache files never contain paths or session ids, only
/// per-record attribution (SHA-256 dedup hash → day/tokens).
///
/// MatchedSessions = discovered session directories whose main wire
/// (agents/main/wire.jsonl) parsed cleanly (readable, zero bad lines) and
/// proved the Kimi wire format (recognized metadata protocol 1.x or at least
/// one valid usage.record). Fork lineage is read minimally from state.json
/// (only the agents map's forkedFrom values); a forkedFrom naming another
/// agent of the same session needs no action (verified: same-session forks
/// carry no inherited usage records), any other shape marks the source
/// partial + lineage_unresolved. Cross-file dedup runs through a per-scan
/// global attribution index: every file (freshly parsed or restored from its
/// cache entry) merges its records first-wins by content hash, so adding a
/// fork after its parent was cached or deleting the parent afterwards both
/// stay correct; a duplicate is counted once and also raises
/// lineage_unresolved, since natural collisions are impossible (records carry
/// millisecond timestamps).
///
/// Memory is bounded per scan: the global attribution index holds at most
/// <c>MaxAttributionRecords</c> = 100_000 entries, and each file's cache
/// record map is merged into it as records parse while the map itself stops
/// growing past <c>MaxCachedRecords</c> = 20_000 distinct records (a larger
/// file keeps counting and matching, it just never becomes cacheable), so
/// per-file memory never scales with file size.
/// </summary>
public sealed class KimiLocalUsageContributor : IAgentLocalUsageContributor
{
    public const string ParserVersion = "kimi-local-wire-usage/v3";

    private const int MaxAttributionRecords = 100_000;
    private const int DefaultMaxCachedRecords = 20_000;
    private const int MaxStateJsonBytes = 1024 * 1024;
    private const int DefaultMaxDiscoveredSessions = 100_000;
    private const string SynthesizedModel = "kimi-local-wire";

    private static readonly AgentLocalUsageDescriptor ContributorDescriptor =
        new("acp-kimi", "Kimi Code", "kimi", "acp-kimi");

    private readonly Func<string?> _homeResolver;
    private readonly IAgentUsageCacheStore _cache;
    private readonly TimeZoneInfo _zone;

    public KimiLocalUsageContributor(
        Func<string?>? homeResolver = null,
        IAgentUsageCacheStore? cacheStore = null,
        TimeZoneInfo? zone = null)
    {
        _homeResolver = homeResolver ?? DefaultHome;
        _cache = cacheStore ?? NullAgentUsageCacheStore.Instance;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public AgentLocalUsageDescriptor Descriptor => ContributorDescriptor;

    /// <summary>Diagnostics/test hook invoked once per wire file with the scan
    /// path taken and the number of bytes actually read for parsing.</summary>
    internal Action<string, KimiWireScanMode, long>? ScanObserver { get; set; }

    /// <summary>Test seam: discovery cap; discovering one session beyond it
    /// reports discovery_truncated (reaching the cap exactly does not).</summary>
    internal int MaxDiscoveredSessions { get; set; } = DefaultMaxDiscoveredSessions;

    /// <summary>Test seam: per-file record cap above which a clean parse is
    /// matched but not cached.</summary>
    internal int MaxCachedRecords { get; set; } = DefaultMaxCachedRecords;

    public AgentUsageSourceStatus Collect(
        IAgentUsageRecordSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var now = DateTimeOffset.Now;
        var counters = new ScanCounters();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var scan = new ScanState();

        var home = _homeResolver();
        var sessionsRoot = string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, "sessions");
        if (sessionsRoot == null || !Directory.Exists(sessionsRoot))
        {
            // A missing/empty home is "available with zero data", never an
            // error: the source stays visible in the report.
            return BuildStatus(now, counters, reasons, expected: 0, matched: 0);
        }

        var sessionDirectories = new List<string>();
        var discoveryTruncated = false;
        try
        {
            foreach (var workDir in new DirectoryInfo(sessionsRoot).EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Junctions/symlinks are never descended into: a linked tree
                // must not be counted twice.
                if (!UsageDiscoveryGuard.ShouldDescend(workDir))
                    continue;
                try
                {
                    foreach (var sessionDir in workDir.EnumerateDirectories())
                    {
                        if (!UsageDiscoveryGuard.ShouldDescend(sessionDir))
                            continue;
                        if (sessionDirectories.Count >= MaxDiscoveredSessions)
                        {
                            // A (cap+1)-th eligible session exists: only this
                            // extra discovery proves truncation. Reaching the
                            // cap exactly at a clean boundary stays unflagged.
                            discoveryTruncated = true;
                            break;
                        }

                        sessionDirectories.Add(sessionDir.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    counters.TraversalFailed = true;
                }

                if (discoveryTruncated)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.TraversalFailed = true;
        }

        if (counters.TraversalFailed)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
        if (discoveryTruncated)
            reasons.Add(AgentUsageGapReason.DiscoveryTruncated);

        // Deterministic order keeps first-wins attribution stable.
        sessionDirectories.Sort(StringComparer.Ordinal);

        var expected = sessionDirectories.Count;
        var matched = 0;
        foreach (var sessionDir in sessionDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProcessSession(sessionDir, counters, reasons, scan, cancellationToken))
                matched++;
        }

        if (scan.LineageUnresolved)
            reasons.Add(AgentUsageGapReason.LineageUnresolved);
        if (counters.SkippedFiles > 0 || counters.BadLines > 0)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);

        // Records are attributed globally first-wins, so only now — after
        // every file merged into the scan index — can day totals be emitted.
        EmitDays(sink, FoldDayTotals(scan));

        return BuildStatus(now, counters, reasons, expected, matched);
    }

    private bool ProcessSession(
        string sessionDir,
        ScanCounters counters,
        HashSet<string> reasons,
        ScanState scan,
        CancellationToken cancellationToken)
    {
        if (HasUnresolvedLineage(sessionDir, counters))
            scan.LineageUnresolved = true;

        var agentsRoot = Path.Combine(sessionDir, "agents");
        var mainWire = Path.Combine(agentsRoot, "main", "wire.jsonl");
        var mainExists = File.Exists(mainWire);
        if (!mainExists)
        {
            counters.SkippedFiles++;
            reasons.Add(AgentUsageGapReason.MissingSessionLogs);
        }

        string[] agentDirectories;
        try
        {
            agentDirectories = Directory.EnumerateDirectories(agentsRoot).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.TraversalFailed = true;
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
            return false;
        }

        Array.Sort(agentDirectories, StringComparer.Ordinal);

        var mainParsedClean = false;
        var mainRecognized = false;
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

            var result = ProcessWire(wirePath, counters, scan, cancellationToken);
            if (string.Equals(Path.GetFileName(agentDir), "main", StringComparison.OrdinalIgnoreCase))
            {
                mainParsedClean = result.ParsedClean;
                mainRecognized = result.Recognized;
            }
        }

        if (!mainExists || !mainParsedClean)
            return false;
        if (!mainRecognized)
        {
            reasons.Add(AgentUsageGapReason.UnsupportedFormat);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads state.json minimally and reports whether any agent declares a
    /// forkedFrom that does not name another agent of the same session. Only
    /// the agents map's forkedFrom values are extracted; every other field
    /// (cwd, title, prompts) is privacy-sensitive and never read, persisted
    /// or emitted.
    /// </summary>
    private static bool HasUnresolvedLineage(string sessionDir, ScanCounters counters)
    {
        var statePath = Path.Combine(sessionDir, "state.json");
        if (!File.Exists(statePath))
            return false;

        try
        {
            using var stream = new FileStream(
                statePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxStateJsonBytes)
            {
                counters.SkippedFiles++;
                return false;
            }

            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var knownAgentIds = new HashSet<string>(StringComparer.Ordinal);
            var forkedFromValues = new List<string>();
            foreach (var agent in agents.EnumerateObject())
            {
                knownAgentIds.Add(agent.Name);
                if (agent.Value.ValueKind == JsonValueKind.Object
                    && agent.Value.TryGetProperty("forkedFrom", out var forkedFrom)
                    && forkedFrom.ValueKind == JsonValueKind.String
                    && forkedFrom.GetString() is { Length: > 0 } value)
                {
                    forkedFromValues.Add(value);
                }
            }

            // Same-session forks (forkedFrom names a sibling agent) carry no
            // inherited usage records, so only unknown shapes are unresolved.
            return forkedFromValues.Any(value => !knownAgentIds.Contains(value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable lineage state: count the session normally and note
            // the skipped file rather than guessing.
            counters.SkippedFiles++;
            return false;
        }
    }

    private WireProcessResult ProcessWire(
        string wirePath,
        ScanCounters counters,
        ScanState scan,
        CancellationToken cancellationToken)
    {
        long size;
        long mtimeTicks;
        try
        {
            var info = new FileInfo(wirePath);
            size = info.Length;
            mtimeTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.SkippedFiles++;
            return WireProcessResult.Failed;
        }

        counters.ScannedFiles++;
        var entryId = AgentUsageCacheEntryId.ForPath(wirePath);
        var entry = _cache.Get(entryId);
        var reusable = entry != null
            && string.Equals(entry.ParserVersion, ParserVersion, StringComparison.Ordinal)
            && string.Equals(entry.TimezoneId, _zone.Id, StringComparison.Ordinal)
            ? entry
            : null;

        if (reusable != null && reusable.FileSize == size && reusable.MtimeUtc == mtimeTicks)
        {
            // Trusted cache hit: restore the entry's attribution into the
            // global index, then re-read only the bytes past the last consumed
            // line (the unterminated tail, never cached) so a still-open file
            // reports its latest record consistently on every scan.
            var probeTail = new Dictionary<string, long>(StringComparer.Ordinal);
            var probe = ParseRange(
                wirePath, reusable.ConsumedLength, mergeCompleteLines: false,
                fileRecords: null, probeTail, scan, cancellationToken);
            ScanObserver?.Invoke(
                wirePath, KimiWireScanMode.CacheHit, size - reusable.ConsumedLength);
            if (!probe.IoFailed)
            {
                counters.BadLines = checked(counters.BadLines + probe.BadLines);
                if (reusable.LineageUnresolved)
                    scan.LineageUnresolved = true;
                MergeFileRecords(scan, reusable.Records);
                foreach (var (day, tokens) in reusable.ResidualDays)
                    AddDay(scan.ResidualDays, day, tokens);
                foreach (var (day, tokens) in probeTail)
                    AddDay(scan.ResidualDays, day, tokens);
                return new WireProcessResult(probe.BadLines == 0, reusable.Recognized);
            }
        }

        Dictionary<string, AgentUsageCacheRecord> fileRecords;
        long parseStart;
        KimiWireScanMode mode;
        var recognizedBase = false;
        var lineageUnresolvedBase = false;
        if (reusable != null
            && size > reusable.ConsumedLength
            && reusable.TailOffset >= 0
            && VerifyTail(wirePath, reusable))
        {
            fileRecords = new Dictionary<string, AgentUsageCacheRecord>(
                reusable.Records, StringComparer.Ordinal);
            // The cached base attribution rides the global index before the
            // increment is folded, exactly as the old end-of-file merge did
            // in insertion order.
            MergeFileRecords(scan, reusable.Records);
            parseStart = reusable.ConsumedLength;
            mode = KimiWireScanMode.Append;
            recognizedBase = reusable.Recognized;
            lineageUnresolvedBase = reusable.LineageUnresolved;
        }
        else
        {
            fileRecords = new Dictionary<string, AgentUsageCacheRecord>(StringComparer.Ordinal);
            parseStart = 0;
            mode = KimiWireScanMode.Full;
        }

        var tailDays = new Dictionary<string, long>(StringComparer.Ordinal);
        var parsed = ParseRange(
            wirePath, parseStart, mergeCompleteLines: true,
            fileRecords, tailDays, scan, cancellationToken);
        ScanObserver?.Invoke(wirePath, mode, Math.Max(0, size - parseStart));
        counters.BadLines = checked(counters.BadLines + parsed.BadLines);
        if (lineageUnresolvedBase || parsed.LineageUnresolved)
            scan.LineageUnresolved = true;

        if (parsed.IoFailed)
        {
            counters.SkippedFiles++;
            return WireProcessResult.Failed;
        }

        var recognized = recognizedBase || parsed.Recognized;
        // Past the record cap the entry map stopped growing, so the file
        // keeps counting and matching but is never cached; it re-parses on
        // every scan instead of degrading completeness.
        if (parsed.BadLines == 0 && !parsed.RecordsOverflowed && fileRecords.Count <= MaxCachedRecords)
        {
            _cache.Save(entryId, new AgentUsageCacheEntry(
                ParserVersion,
                _zone.Id,
                size,
                mtimeTicks,
                parsed.ConsumedLength,
                parsed.TailOffset,
                parsed.TailHash,
                recognized,
                new Dictionary<string, AgentUsageCacheRecord>(fileRecords, StringComparer.Ordinal),
                new Dictionary<string, long>(StringComparer.Ordinal),
                LineageUnresolved: lineageUnresolvedBase || parsed.LineageUnresolved));
        }

        foreach (var (day, tokens) in tailDays)
            AddDay(scan.ResidualDays, day, tokens);
        return new WireProcessResult(parsed.BadLines == 0, recognized);
    }

    /// <summary>
    /// Parses the byte range [startOffset, end) of a wire file. Complete
    /// (newline-terminated) usage records (when <paramref name="mergeCompleteLines"/>)
    /// merge into the bounded global attribution index as they parse and into
    /// <paramref name="fileRecords"/> only while that entry map still fits
    /// under <see cref="MaxCachedRecords"/> — past the cap the map stops
    /// growing, the records keep counting, and the file becomes uncached.
    /// The unterminated final line merges into <paramref name="tailDays"/>
    /// only, so it is reported on this scan but never cached — it is re-read
    /// once the append completes the line.
    /// </summary>
    private RangeParseResult ParseRange(
        string wirePath,
        long startOffset,
        bool mergeCompleteLines,
        Dictionary<string, AgentUsageCacheRecord>? fileRecords,
        Dictionary<string, long> tailDays,
        ScanState scan,
        CancellationToken cancellationToken)
    {
        var result = new RangeParseResult
        {
            ConsumedLength = startOffset,
            TailOffset = -1
        };

        try
        {
            var oversized = BoundedJsonlReader.Read(
                wirePath,
                startOffset,
                (content, isUnterminatedFinalLine, lineStart, rawLength) =>
                {
                    var line = KimiWireUsageParser.ParseLine(content);
                    switch (line.Kind)
                    {
                        case KimiWireLineKind.Invalid:
                            result.BadLines++;
                            break;
                        case KimiWireLineKind.MetadataRecognized:
                            result.Recognized = true;
                            break;
                        case KimiWireLineKind.UsageRecord:
                            result.Recognized = true;
                            var tokens = checked(
                                line.Record!.InputTokens
                                + line.Record.OutputTokens
                                + line.Record.CacheReadTokens
                                + line.Record.CacheCreationTokens);
                            var day = LocalDay(line.Record.Timestamp);
                            if (isUnterminatedFinalLine || !mergeCompleteLines)
                            {
                                AddDay(tailDays, day, tokens);
                            }
                            else
                            {
                                // Merge into the bounded global attribution
                                // index as the record parses; the per-file
                                // entry map only keeps records while it fits
                                // under the cache cap.
                                var hash = Convert.ToHexString(
                                    SHA256.HashData(content.Span)).ToLowerInvariant();
                                var record = new AgentUsageCacheRecord(day, tokens);
                                var duplicateInFile = fileRecords?.ContainsKey(hash) == true;
                                MergeRecord(scan, hash, record);
                                if (duplicateInFile)
                                    result.LineageUnresolved = true;
                                if (fileRecords is { } entries && !result.RecordsOverflowed)
                                {
                                    if (!entries.ContainsKey(hash))
                                    {
                                        if (entries.Count < MaxCachedRecords)
                                            entries[hash] = record;
                                        else
                                            result.RecordsOverflowed = true;
                                    }
                                }
                            }

                            break;
                    }

                    if (!isUnterminatedFinalLine && mergeCompleteLines)
                    {
                        result.TailOffset = lineStart;
                        result.TailHash = HashRawLine(content, rawLength);
                        result.ConsumedLength = lineStart + rawLength;
                    }
                },
                cancellationToken);
            result.BadLines = checked(result.BadLines + oversized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.IoFailed = true;
        }

        return result;
    }

    /// <summary>
    /// Merges one file's cached records into the bounded global attribution
    /// index, first-wins.
    /// </summary>
    private static void MergeFileRecords(
        ScanState scan,
        IReadOnlyDictionary<string, AgentUsageCacheRecord> records)
    {
        foreach (var (hash, record) in records)
            MergeRecord(scan, hash, record);
    }

    /// <summary>
    /// Merges one record into the bounded global attribution index,
    /// first-wins. A hash already contributed by an earlier file (or an
    /// earlier record of the same file) means inherited fork content: it is
    /// counted once and flags unresolved lineage. Exceeding the cap also
    /// flags unresolved lineage and counts the record through the residual
    /// totals rather than dropping it.
    /// </summary>
    private static void MergeRecord(
        ScanState scan,
        string hash,
        AgentUsageCacheRecord record)
    {
        if (scan.Attribution.Count >= MaxAttributionRecords)
        {
            scan.LineageUnresolved = true;
            AddDay(scan.ResidualDays, record.Day, record.Tokens);
            return;
        }

        if (!scan.Attribution.TryAdd(hash, record))
            scan.LineageUnresolved = true;
    }

    /// <summary>
    /// Hash of the exact on-disk bytes of a complete line, terminator included
    /// ("\n" or "\r\n", reconstructed from the raw length). Verification seeks
    /// to the stored offset, re-reads up to and including the next "\n" and
    /// compares hashes, so an in-place rewrite or truncation is detected.
    /// </summary>
    private static string HashRawLine(ReadOnlyMemory<byte> content, int rawLength)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(content.Span);
        hasher.AppendData(
            Encoding.ASCII.GetBytes(rawLength - content.Length == 2 ? "\r\n" : "\n"));
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool VerifyTail(string wirePath, AgentUsageCacheEntry entry)
    {
        try
        {
            using var stream = new FileStream(
                wirePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(entry.TailOffset, SeekOrigin.Begin);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long consumed = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return false;
                var newlineIndex = buffer.AsSpan(0, read).IndexOf((byte)'\n');
                var take = newlineIndex >= 0 ? newlineIndex + 1 : read;
                consumed += take;
                if (consumed > BoundedJsonlReader.MaxLineBytes + 2)
                    return false;
                hasher.AppendData(buffer, 0, take);
                if (newlineIndex >= 0)
                    break;
            }

            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(hash, entry.TailHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<string, long> FoldDayTotals(ScanState scan)
    {
        var totals = new Dictionary<string, long>(scan.ResidualDays, StringComparer.Ordinal);
        foreach (var record in scan.Attribution.Values)
            AddDay(totals, record.Day, record.Tokens);
        return totals;
    }

    private void EmitDays(IAgentUsageRecordSink sink, IReadOnlyDictionary<string, long> totals)
    {
        // One synthetic record per day at local noon; the accumulator sums all
        // four token fields, so the whole day total rides on InputTokens.
        foreach (var (day, tokens) in totals)
            sink.Add(new AgentUsageRecord(LocalNoon(day), SynthesizedModel, tokens, 0, 0, 0));
    }

    private string LocalDay(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, _zone).DateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTimeOffset LocalNoon(string day)
    {
        var noon = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(noon, _zone.GetUtcOffset(noon));
    }

    private static void AddDay(Dictionary<string, long> days, string day, long tokens) =>
        days[day] = days.TryGetValue(day, out var existing)
            ? checked(existing + tokens)
            : tokens;

    private AgentUsageSourceStatus BuildStatus(
        DateTimeOffset now,
        ScanCounters counters,
        HashSet<string> reasons,
        int expected,
        int matched)
    {
        // Unavailable only when the sessions root exists but literally nothing
        // could be read; an empty tree stays "available with zero data".
        var status = counters.ScannedFiles == 0
            && (counters.SkippedFiles > 0 || counters.TraversalFailed)
            ? AgentUsageSourceStatus.Unavailable
            : reasons.Count > 0
                ? AgentUsageSourceStatus.Partial
                : AgentUsageSourceStatus.Available;

        return new AgentUsageSourceStatus(
            Descriptor.Key,
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
            Reasons = AgentUsageGapReason.Ordered.Where(reasons.Contains).ToArray()
        };
    }

    private static string? DefaultHome()
    {
        var overridden = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".kimi-code");
    }

    private sealed class ScanCounters
    {
        public int ScannedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int BadLines { get; set; }
        public bool TraversalFailed { get; set; }
    }

    private sealed class ScanState
    {
        public Dictionary<string, AgentUsageCacheRecord> Attribution { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ResidualDays { get; } = new(StringComparer.Ordinal);
        public bool LineageUnresolved { get; set; }
    }

    private sealed class RangeParseResult
    {
        public bool IoFailed { get; set; }
        public int BadLines { get; set; }
        public bool Recognized { get; set; }
        public bool RecordsOverflowed { get; set; }
        public bool LineageUnresolved { get; set; }
        public long ConsumedLength { get; set; }
        public long TailOffset { get; set; }
        public string? TailHash { get; set; }
    }

    private readonly record struct WireProcessResult(bool ParsedClean, bool Recognized)
    {
        public static WireProcessResult Failed => new(false, false);
    }
}
