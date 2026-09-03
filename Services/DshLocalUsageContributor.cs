using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PSX.Models;

namespace PSX.Services;

internal enum DshSessionScanMode
{
    CacheHit,
    Extract
}

/// <summary>
/// Local-all usage contributor for DeepSeek Harness: counts every session
/// found under <c>DSH_HOME ?? ~/.dsh</c> (<c>sessions/**/session.jsonl.zstd</c>),
/// independently of the ThreadStore. The compressed files are decoded by the
/// bundled extractor (<see cref="IDshSessionExtractor"/>), which streams
/// protocol-v3 events; this class owns the counting semantics: within a file,
/// streaming samples are folded per attempt (the last assistant/message wins,
/// otherwise the chunk with the highest seq), distinct attempts accumulate —
/// the attempt rides the fold key even when a message id is present, so retry
/// attempts sharing one id stay separate groups — the winning samples are
/// deduplicated globally by a per-scan attribution index keyed by content hash
/// (message id + attempt hash, or a canonical turn/step/attempt/token hash
/// when no id exists), a hash seen in a second file (inherited fork content)
/// is counted once and flags lineage_unresolved, and cacheWriteTokens map to
/// cache-creation tokens. Events with neither message id nor turn/step are
/// non-dedupable and ride the residual day totals. Per-file attribution
/// records live in the disposable disk cache keyed by path hash, so restored
/// entries re-run the same global first-wins dedup on every scan; entries
/// embed the extractor protocol version so an extractor upgrade rebuilds them,
/// and they are saved only after a clean full parse (no truncation, no
/// malformed lines and no stray bytes outside any frame).
///
/// Each file commits incrementally: at its EOF the sink folds the file's
/// winners into the shared per-scan attribution index and residual day map
/// while its cache entry is written to a batch-private disk namespace. A
/// per-batch rollback journal records only hashes, residual-day deltas and
/// integer counter deltas. A failed batch (null hello) discards the staged
/// cache namespace and undoes its scan state; a successful batch promotes
/// only already-validated entries into the live cache.
///
/// Peak memory is bounded by the global attribution index
/// (<c>MaxAttributionRecords</c> = 100_000 records), the rollback journal (at
/// most that many hash strings, one delta per day the batch touched and five
/// integer counter deltas), and one file's fold state (at most
/// <c>MaxFoldGroups</c> = 50_000 in-memory sample groups and
/// <c>MaxCachedRecords</c> = 20_000 cache records). Additional fold groups
/// use recursively partitioned transient disk storage containing hashes and
/// numeric usage only, so final-sample-wins remains exact without unbounded
/// memory.
///
/// MatchedSessions = discovered session files that contributed through a
/// cache hit or a clean parse, independent of whether the record cap allowed
/// caching the entry. When the extractor is unavailable the cached
/// files whose size+mtime still match keep contributing: any usable data
/// yields partial + extractor_unavailable, a non-empty sessions tree with
/// zero usable data yields unavailable, and a missing/empty tree stays
/// "available with zero data".
/// </summary>
public sealed class DshLocalUsageContributor : IAgentLocalUsageContributor
{
    public const string ParserVersion = "dsh-session-usage/v4";

    internal const int MaxBatchFiles = 500;

    private const int MaxAttributionRecords = 100_000;
    private const int DefaultMaxCachedRecords = 20_000;
    private const int DefaultMaxFoldGroups = 50_000;
    private const int DefaultMaxDiscoveredFiles = 100_000;
    private const int DefaultMaxDiscoveredDirectories = 20_000;
    private const string FinalSampleKind = "assistant/message";
    private const string SynthesizedModel = "dsh-session-usage";

    private static readonly AgentLocalUsageDescriptor ContributorDescriptor =
        new("local-dsh", "DeepSeek Harness", "dsh", null);

    private readonly Func<string?> _homeResolver;
    private readonly IDshSessionExtractor? _extractor;
    private readonly IAgentUsageCacheStore _cache;
    private readonly TimeZoneInfo _zone;
    private readonly Func<string?> _foldSpillRootResolver;

    public DshLocalUsageContributor(
        Func<string?>? homeResolver = null,
        IDshSessionExtractor? extractor = null,
        IAgentUsageCacheStore? cacheStore = null,
        TimeZoneInfo? zone = null,
        Func<string?>? foldSpillRootResolver = null)
    {
        _homeResolver = homeResolver ?? DefaultHome;
        _extractor = extractor;
        _cache = cacheStore ?? NullAgentUsageCacheStore.Instance;
        _zone = zone ?? TimeZoneInfo.Local;
        _foldSpillRootResolver = foldSpillRootResolver ?? DefaultFoldSpillRoot;
    }

    public AgentLocalUsageDescriptor Descriptor => ContributorDescriptor;

    /// <summary>Diagnostics/test hook invoked once per session file with the
    /// scan path taken.</summary>
    internal Action<string, DshSessionScanMode>? ScanObserver { get; set; }

    /// <summary>Test seam: per-file record cap above which a clean parse is
    /// matched but not cached.</summary>
    internal int MaxCachedRecords { get; set; } = DefaultMaxCachedRecords;

    /// <summary>Test seam: cap on distinct fold groups held in memory for one
    /// file; further new groups use the bounded disk fold store.</summary>
    internal int MaxFoldGroups { get; set; } = DefaultMaxFoldGroups;

    /// <summary>Test seam: discovery caps; discovering one item beyond either
    /// one reports discovery_truncated (reaching a cap exactly at a clean
    /// boundary does not).</summary>
    internal int MaxDiscoveredFiles { get; set; } = DefaultMaxDiscoveredFiles;

    internal int MaxDiscoveredDirectories { get; set; } = DefaultMaxDiscoveredDirectories;

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
            return BuildStatus(now, counters, reasons, expected: 0);
        }

        var files = EnumerateSessionFiles(sessionsRoot, counters, cancellationToken);
        if (counters.TraversalFailed)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);
        if (counters.DiscoveryTruncated)
            reasons.Add(AgentUsageGapReason.DiscoveryTruncated);

        var expected = files.Count;
        if (expected == 0)
            return BuildStatus(now, counters, reasons, expected: 0);

        // Probe the extractor protocol version with an empty batch (a single
        // hello line). The version rides in the cache parser tag, so an
        // extractor upgrade rebuilds every entry.
        var probe = _extractor?.ExtractBatch(
            [], NullDshExtractionEventSink.Instance, cancellationToken);
        var parserTag = probe == null
            ? null
            : $"{ParserVersion}+extractor/{probe.ProtocolVersion}";
        if (parserTag == null)
            reasons.Add(AgentUsageGapReason.ExtractorUnavailable);

        var toParse = new List<PendingFile>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size;
            long mtimeTicks;
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                mtimeTicks = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                counters.SkippedFiles++;
                counters.UnreadableFiles++;
                continue;
            }

            counters.ScannedFiles++;
            var entry = _cache.Get(AgentUsageCacheEntryId.ForPath(path));
            if (IsReusable(entry, parserTag, size, mtimeTicks))
            {
                RestoreEntry(scan, entry!);
                ScanObserver?.Invoke(path, DshSessionScanMode.CacheHit);
                counters.Matched++;
                counters.Contributing++;
                continue;
            }

            toParse.Add(new PendingFile(path, size, mtimeTicks));
        }

        for (var offset = 0; offset < toParse.Count && parserTag != null; offset += MaxBatchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = toParse.GetRange(offset, Math.Min(MaxBatchFiles, toParse.Count - offset));
            // Incremental commit with rollback: every file folds into the
            // scan state, cache and counters as its EOF arrives; a failed
            // batch is undone from its journal so nothing of it persists.
            var journal = new BatchJournal();
            using var cacheBatch = _cache.BeginBatch();
            using var batch = new BatchSink(
                this, chunk, scan, counters, reasons, journal, cacheBatch, parserTag!);
            var hello = _extractor!.ExtractBatch(
                chunk.Select(file => file.Path).ToArray(), batch, cancellationToken);
            if (hello == null)
            {
                // Process start failure, non-zero exit or a protocol
                // violation: the batch's merges, cache writes and counters
                // roll back, so it contributes nothing.
                RollbackBatch(scan, counters, journal);
                reasons.Add(AgentUsageGapReason.ExtractorUnavailable);
                break;
            }

            // Staged cache entries become visible only after the extractor
            // has validated the complete process/protocol result.
            cacheBatch.Commit();
        }

        if (scan.LineageUnresolved)
            reasons.Add(AgentUsageGapReason.LineageUnresolved);
        if (counters.UnreadableFiles > 0 || counters.BadLines > 0)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);

        // Records are attributed globally first-wins, so only now — after
        // every file merged into the scan index — can day totals be emitted.
        EmitDays(sink, FoldDayTotals(scan));

        return BuildStatus(now, counters, reasons, expected);
    }

    private List<string> EnumerateSessionFiles(
        string sessionsRoot,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(sessionsRoot);
        var visitedDirectories = 0;
        var fileCapExceeded = false;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visitedDirectories >= MaxDiscoveredDirectories)
            {
                // Another directory waits unvisited beyond the cap: the walk
                // cannot prove the tree ends here, so it is truncated.
                counters.DiscoveryTruncated = true;
                break;
            }

            var directory = pending.Pop();
            visitedDirectories++;
            string[] matches;
            DirectoryInfo[] subdirectories;
            try
            {
                var directoryInfo = new DirectoryInfo(directory);
                subdirectories = directoryInfo.GetDirectories();
                matches = Directory.GetFiles(directory, "session.jsonl.zstd");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                counters.TraversalFailed = true;
                continue;
            }

            foreach (var match in matches)
            {
                // The list already holds `cap` files and another one exists:
                // only this (cap+1)-th discovery proves truncation. Reaching
                // the cap exactly at a clean boundary stays unflagged.
                if (files.Count >= MaxDiscoveredFiles)
                {
                    counters.DiscoveryTruncated = true;
                    fileCapExceeded = true;
                    break;
                }

                files.Add(match);
            }

            if (fileCapExceeded)
                break;

            // Junctions/symlinks are never descended into: a linked tree must
            // not be counted twice or loop the walk.
            foreach (var subdirectory in subdirectories)
            {
                if (UsageDiscoveryGuard.ShouldDescend(subdirectory))
                    pending.Push(subdirectory.FullName);
            }
        }

        // Deterministic order keeps first-wins attribution stable.
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// Undoes a failed batch from its journal: removes the attribution hashes
    /// it added, subtracts its residual day deltas, deletes the cache entries
    /// it wrote and reverses its counter deltas. Reason and lineage flags
    /// deliberately stay — they only widen the uncertainty envelope, which is
    /// the conservative direction.
    /// </summary>
    private void RollbackBatch(ScanState scan, ScanCounters counters, BatchJournal journal)
    {
        foreach (var hash in journal.AddedHashes)
            scan.Attribution.Remove(hash);
        foreach (var (day, delta) in journal.ResidualDeltas)
        {
            if (scan.ResidualDays.TryGetValue(day, out var total))
            {
                var remaining = total - delta;
                if (remaining > 0)
                    scan.ResidualDays[day] = remaining;
                else
                    scan.ResidualDays.Remove(day);
            }
        }
        counters.Matched -= journal.Matched;
        counters.Contributing -= journal.Contributing;
        counters.BadLines -= journal.BadLines;
        counters.SkippedFiles -= journal.SkippedFiles;
        counters.UnreadableFiles -= journal.UnreadableFiles;
    }

    private bool IsReusable(AgentUsageCacheEntry? entry, string? parserTag, long size, long mtimeTicks)
    {
        if (entry == null
            || entry.FileSize != size
            || entry.MtimeUtc != mtimeTicks
            || !string.Equals(entry.TimezoneId, _zone.Id, StringComparison.Ordinal))
        {
            return false;
        }

        // With the extractor unavailable the exact tag cannot be recomputed;
        // accept any entry written by this parser family so cached files keep
        // contributing through an outage.
        return parserTag != null
            ? string.Equals(entry.ParserVersion, parserTag, StringComparison.Ordinal)
            : entry.ParserVersion.StartsWith(ParserVersion + "+extractor/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Merges a cached file's attribution into the per-scan global index.
    /// The same first-wins dedup that ran when the entry was written re-runs
    /// here, so add-fork/delete-parent combinations stay correct across
    /// hot/cold cache mixes. Cache-hit merges precede every batch and are
    /// never journaled or rolled back.
    /// </summary>
    private static void RestoreEntry(ScanState scan, AgentUsageCacheEntry entry)
    {
        if (entry.LineageUnresolved)
            scan.LineageUnresolved = true;
        foreach (var (hash, record) in entry.Records)
            MergeRecord(scan, hash, record.Day, record.Tokens);
        foreach (var (day, tokens) in entry.ResidualDays)
            AddResidualDay(scan, day, tokens, journal: null);
    }

    private static void MergeRecord(
        ScanState scan,
        string hash,
        string day,
        long tokens,
        BatchJournal? journal = null)
    {
        if (scan.Attribution.Count >= MaxAttributionRecords)
        {
            // Dedup budget exhausted: count the record but flag the
            // uncertainty rather than dropping it silently.
            scan.LineageUnresolved = true;
            AddResidualDay(scan, day, tokens, journal);
            return;
        }

        if (scan.Attribution.TryAdd(hash, new AgentUsageCacheRecord(day, tokens)))
        {
            journal?.AddedHashes.Add(hash);
        }
        else
        {
            // The same record in a second file (or a second group within one
            // file) means inherited fork content: count it once, flag the
            // uncertainty.
            scan.LineageUnresolved = true;
        }
    }

    private static void AddResidualDay(
        ScanState scan,
        string day,
        long tokens,
        BatchJournal? journal)
    {
        AddDay(scan.ResidualDays, day, tokens);
        if (journal != null)
        {
            journal.ResidualDeltas[day] = journal.ResidualDeltas.TryGetValue(day, out var prior)
                ? checked(prior + tokens)
                : tokens;
        }
    }

    /// <summary>
    /// Attempt-aware fold key: one group = one attempt. The attempt survives
    /// even when a message id is present, so retry attempts sharing one id
    /// stay separate groups whose winners each accumulate.
    /// </summary>
    private readonly record struct SampleKey(
        string? MessageId,
        long? Turn,
        long? Step,
        long Attempt,
        bool AttemptSynthetic);

    /// <summary>
    /// The currently winning sample for one attempt. Chunks rank by seq
    /// (null seq sorts lowest, ties keep the earlier arrival); any
    /// assistant/message event outranks every chunk, and among finals the
    /// last arrival wins.
    /// </summary>
    private sealed class SampleCandidate
    {
        public FoldSample? Final { get; set; }
        public FoldSample? BestChunk { get; set; }
        public long BestChunkSeq { get; set; } = -1;

        public void Add(FoldSample usage)
        {
            if (usage.IsFinal)
            {
                Final = usage;
                return;
            }

            var seq = usage.Seq ?? -1;
            if (BestChunk == null || seq >= BestChunkSeq)
            {
                BestChunk = usage;
                BestChunkSeq = seq;
            }
        }

        public FoldSample? Winner => Final ?? BestChunk;
    }

    /// <summary>Minimal fold projection. Group and attribution identities are
    /// hashes, so spill files never contain message or session identifiers.</summary>
    private sealed record FoldSample(
        string GroupHash,
        string? DedupHash,
        long? TimestampMs,
        bool IsFinal,
        long? Seq,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens);

    private static SampleKey KeyFor(DshExtractedUsage usage) =>
        usage.MessageId != null
            ? new SampleKey(
                usage.MessageId, null, null, usage.Attempt ?? 0, usage.AttemptSynthetic)
            : new SampleKey(
                null, usage.Turn, usage.Step, usage.Attempt ?? 0, usage.AttemptSynthetic);

    private static FoldSample FoldSampleFor(DshExtractedUsage usage, SampleKey key) =>
        new(
            key.MessageId != null
                ? HashString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"m|{EncodeHashPart(key.MessageId)}|{key.Attempt}|{key.AttemptSynthetic}"))
                : HashString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"t|{key.Turn}|{key.Step}|{key.Attempt}|{key.AttemptSynthetic}")),
            DedupHashFor(usage),
            usage.TimestampMs,
            string.Equals(usage.Kind, FinalSampleKind, StringComparison.Ordinal),
            usage.Seq,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.CacheCreationTokens);

    private static string? DedupHashFor(DshExtractedUsage usage)
    {
        if (usage.MessageId != null)
        {
            // The attempt rides the hash so distinct retry attempts of one
            // message id survive per-file and cross-file dedup separately.
            return HashString(string.Create(
                CultureInfo.InvariantCulture,
                $"{EncodeHashPart(usage.MessageId)}|"
                + $"{(usage.AttemptSynthetic ? "s" : "e")}|{usage.Attempt ?? 0}"));
        }

        if (usage.Turn != null && usage.Step != null)
        {
            return HashString(string.Create(
                CultureInfo.InvariantCulture,
                $"{EncodeHashPart(usage.Kind)}|{usage.Turn.Value}|{usage.Step.Value}|"
                + $"{(usage.AttemptSynthetic ? "s" : "e")}|{usage.Attempt ?? 0}|"
                + $"{usage.InputTokens}|{usage.OutputTokens}|"
                + $"{usage.CacheReadTokens}|{usage.CacheCreationTokens}"));
        }

        return null;
    }

    private static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string EncodeHashPart(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Receives the streaming events of one batch and commits each file as
    /// soon as its EOF arrives: the file's winners fold into the shared scan
    /// state, its residual days merge into the shared day totals, its counters
    /// tick and its cache entry is staged through <see cref="IAgentUsageCacheBatch"/>.
    /// The scan changes are mirrored into <see cref="BatchJournal"/>, while
    /// cache staging remains invisible until the whole extractor batch succeeds.
    /// Events for one file are contiguous, so per-file fold state lives
    /// only between begin and eof and is capped by
    /// <see cref="MaxFoldGroups"/> groups and
    /// <see cref="MaxCachedRecords"/> cache records.
    /// </summary>
    private sealed class BatchSink(
        DshLocalUsageContributor owner,
        IReadOnlyList<PendingFile> chunk,
        ScanState scan,
        ScanCounters counters,
        HashSet<string> reasons,
        BatchJournal journal,
        IAgentUsageCacheBatch cacheBatch,
        string parserTag) : IDshExtractionEventSink, IDisposable
    {
        private readonly Dictionary<SampleKey, SampleCandidate> _samples = new();
        private readonly Dictionary<string, AgentUsageCacheRecord> _fileRecords = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _fileResidualDays = new(StringComparer.Ordinal);
        private OverflowFoldStore? _overflow;
        private int _currentFile = -1;
        private bool _fileHasParent;
        private bool _fileErrored;
        private bool _foldSpillFailed;
        private bool _recordsOverflowed;
        private int _badEvents;

        public void OnHead(DshExtractedHead head)
        {
            BeginFile(head.File);
            if (head.HasParent)
            {
                // Positively identified fork whose inherited increment cannot
                // be proven; events still count and cross-file dedup drops
                // inherited duplicates.
                _fileHasParent = true;
            }
        }

        public void OnUsage(DshExtractedUsage usage)
        {
            BeginFile(usage.File);
            var key = KeyFor(usage);
            if (key.MessageId == null && (key.Turn == null || key.Step == null))
            {
                // Neither message id nor attempt coordinates: the event is
                // non-dedupable and folds into the residual day totals.
                if (!TryAddTokens(_fileResidualDays, usage))
                    _badEvents++;
                return;
            }

            var sample = FoldSampleFor(usage, key);
            if (_samples.TryGetValue(key, out var existing))
            {
                existing.Add(sample);
                return;
            }

            if (_samples.Count >= owner.MaxFoldGroups)
            {
                // New groups beyond the in-memory budget spill as a minimal,
                // identifier-free projection. EOF folds the spill in bounded
                // hash partitions, preserving final-sample-wins exactly.
                if (_foldSpillFailed)
                    return;
                try
                {
                    _overflow ??= new OverflowFoldStore(
                        owner._foldSpillRootResolver(), owner.MaxFoldGroups);
                    if (!_overflow.TryAdd(sample))
                        MarkFoldSpillFailed();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    MarkFoldSpillFailed();
                }
                return;
            }

            var candidate = new SampleCandidate();
            candidate.Add(sample);
            _samples[key] = candidate;
        }

        public void OnEof(DshExtractedEof eof)
        {
            BeginFile(eof.File);
            if (_fileErrored)
                return;

            // A fork whose inherited increment cannot be proven remains
            // incomplete across cache hits. Fold overflow itself is exact:
            // its winners are recovered from the bounded disk spill below.
            if (_fileHasParent)
                scan.LineageUnresolved = true;

            var winners = _samples.Values
                .Select(candidate => candidate.Winner)
                .OfType<FoldSample>();
            foreach (var winner in winners)
                CommitWinner(winner);

            if (_overflow != null && !_overflow.TryFold(CommitWinner))
                MarkFoldSpillFailed();
            _overflow?.Dispose();
            _overflow = null;

            // Garbage bytes outside any frame count as one bad line, exactly
            // like malformed content: the parsed events still count but the
            // file is partial — neither matched nor cached.
            var badLines = checked(_badEvents + eof.Malformed + (eof.Stray > 0 ? 1 : 0));
            var clean = !eof.Truncated && eof.Malformed == 0 && eof.Stray == 0 && _badEvents == 0;
            var file = chunk[_currentFile];

            foreach (var (day, tokens) in _fileResidualDays)
                AddResidualDay(scan, day, tokens, journal);

            counters.BadLines = checked(counters.BadLines + badLines);
            journal.BadLines = checked(journal.BadLines + badLines);
            counters.Contributing++;
            journal.Contributing++;
            if (clean)
            {
                counters.Matched++;
                journal.Matched++;
            }
            else
            {
                counters.SkippedFiles++;
                counters.UnreadableFiles++;
                journal.SkippedFiles++;
                journal.UnreadableFiles++;
            }

            owner.ScanObserver?.Invoke(file.Path, DshSessionScanMode.Extract);

            // A clean parse past the record cap is still matched; only the
            // cache write is conditional on the cap, so the file re-parses
            // every scan instead of degrading completeness.
            if (clean && !_recordsOverflowed)
            {
                var entryId = AgentUsageCacheEntryId.ForPath(file.Path);
                cacheBatch.Save(entryId, new AgentUsageCacheEntry(
                    parserTag,
                    owner._zone.Id,
                    file.Size,
                    file.MtimeTicks,
                    file.Size,
                    -1,
                    null,
                    true,
                    new Dictionary<string, AgentUsageCacheRecord>(_fileRecords, StringComparer.Ordinal),
                    new Dictionary<string, long>(_fileResidualDays, StringComparer.Ordinal),
                    LineageUnresolved: _fileHasParent));
            }
        }

        public void Dispose()
        {
            _overflow?.Dispose();
            _overflow = null;
        }

        public void OnError(DshExtractedError error)
        {
            BeginFile(error.File);
            // A per-file failure never aborts the rest of the batch.
            _fileErrored = true;
            counters.SkippedFiles++;
            journal.SkippedFiles++;
            if (error.Code is "bad_format" or "decode_failed")
                reasons.Add(AgentUsageGapReason.UnsupportedFormat);
            else
            {
                counters.UnreadableFiles++;
                journal.UnreadableFiles++;
            }
        }

        private void BeginFile(int file)
        {
            if (file == _currentFile)
                return;

            // Events for one file are contiguous; a new file index closes the
            // previous file's fold state.
            _overflow?.Dispose();
            _overflow = null;
            _samples.Clear();
            _fileRecords.Clear();
            _fileResidualDays.Clear();
            _currentFile = file;
            _fileHasParent = false;
            _fileErrored = false;
            _foldSpillFailed = false;
            _recordsOverflowed = false;
            _badEvents = 0;
        }

        private void CommitWinner(FoldSample winner)
        {
            if (winner.DedupHash == null || !TryWinningSample(winner, out var record))
            {
                _badEvents++;
                return;
            }

            // Fold into the shared attribution index now, journaling the
            // addition so a failed extractor batch can be rolled back.
            MergeRecord(scan, winner.DedupHash, record.Day, record.Tokens, journal);

            // The file records everything it owns, even records the global
            // index attributes to an earlier file. Past the cache cap the
            // file remains exact for this scan but is deliberately uncached.
            if (!_fileRecords.ContainsKey(winner.DedupHash))
            {
                if (_fileRecords.Count < owner.MaxCachedRecords)
                    _fileRecords[winner.DedupHash] = record;
                else
                    _recordsOverflowed = true;
            }
        }

        private void MarkFoldSpillFailed()
        {
            if (_foldSpillFailed)
                return;
            _foldSpillFailed = true;
            _badEvents++;
            _overflow?.Dispose();
            _overflow = null;
        }

        private bool TryWinningSample(FoldSample usage, out AgentUsageCacheRecord record)
        {
            record = null!;
            if (usage.TimestampMs == null)
                return false;

            DateTimeOffset timestamp;
            long tokens;
            try
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(usage.TimestampMs.Value);
                tokens = checked(
                    usage.InputTokens
                    + usage.OutputTokens
                    + usage.CacheReadTokens
                    + usage.CacheCreationTokens);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                // An out-of-range timestamp or token sum degrades this event
                // to a bad line instead of failing the scan.
                return false;
            }

            record = new AgentUsageCacheRecord(owner.LocalDay(timestamp), tokens);
            return true;
        }

        private bool TryAddTokens(Dictionary<string, long> days, DshExtractedUsage usage)
        {
            if (usage.TimestampMs == null)
                return false;

            DateTimeOffset timestamp;
            long tokens;
            try
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(usage.TimestampMs.Value);
                tokens = checked(
                    usage.InputTokens
                    + usage.OutputTokens
                    + usage.CacheReadTokens
                    + usage.CacheCreationTokens);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                return false;
            }

            AddDay(days, owner.LocalDay(timestamp), tokens);
            return true;
        }
    }

    /// <summary>
    /// Disk-backed continuation of one file's fold map. Events contain only
    /// hashed identities and numeric usage fields. A partition is loaded only
    /// when it has at most the configured number of distinct groups; larger
    /// partitions split recursively by the next SHA-256 nibble.
    /// </summary>
    private sealed class OverflowFoldStore : IDisposable
    {
        private readonly string _directory;
        private readonly string _rootFile;
        private readonly int _maxGroups;
        private BinaryWriter? _writer;

        public OverflowFoldStore(string? rootDirectory, int maxGroups)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("A fold spill root is required.", nameof(rootDirectory));
            _maxGroups = Math.Max(1, maxGroups);
            Directory.CreateDirectory(rootDirectory);
            _directory = Path.Combine(rootDirectory, ".fold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _rootFile = Path.Combine(_directory, "events.bin");
        }

        public bool TryAdd(FoldSample sample)
        {
            try
            {
                _writer ??= NewWriter(_rootFile);
                WriteSample(_writer, sample);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool TryFold(Action<FoldSample> acceptWinner)
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
                return !File.Exists(_rootFile) || FoldPartition(_rootFile, 0, acceptWinner);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // Cleanup remains best-effort after a spill failure.
            }
            _writer = null;
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (Exception)
            {
                // A transient spill is never read by a later scan.
            }
        }

        private bool FoldPartition(string path, int depth, Action<FoldSample> acceptWinner)
        {
            var candidates = new Dictionary<string, SampleCandidate>(StringComparer.Ordinal);
            var needsSplit = false;
            using (var reader = NewReader(path))
            {
                while (TryReadSample(reader, out var sample))
                {
                    if (!candidates.TryGetValue(sample.GroupHash, out var candidate))
                    {
                        if (candidates.Count >= _maxGroups)
                        {
                            needsSplit = true;
                            break;
                        }
                        candidate = new SampleCandidate();
                        candidates[sample.GroupHash] = candidate;
                    }
                    candidate.Add(sample);
                }
            }

            if (!needsSplit)
            {
                foreach (var winner in candidates.Values
                    .Select(candidate => candidate.Winner)
                    .OfType<FoldSample>())
                {
                    acceptWinner(winner);
                }
                File.Delete(path);
                return true;
            }

            // A SHA-256 collision across more than _maxGroups distinct keys
            // would be required to exhaust all 64 nibbles.
            if (depth >= 64)
                return false;

            var childPaths = new Dictionary<char, string>();
            var childWriters = new Dictionary<char, BinaryWriter>();
            try
            {
                using var reader = NewReader(path);
                while (TryReadSample(reader, out var sample))
                {
                    var bucket = sample.GroupHash[depth];
                    if (!childWriters.TryGetValue(bucket, out var writer))
                    {
                        var childPath = Path.Combine(
                            _directory, $"p{depth:D2}-{Guid.NewGuid():N}-{bucket}.bin");
                        childPaths[bucket] = childPath;
                        writer = NewWriter(childPath);
                        childWriters[bucket] = writer;
                    }
                    WriteSample(writer, sample);
                }
            }
            finally
            {
                foreach (var writer in childWriters.Values)
                    writer.Dispose();
            }

            File.Delete(path);
            foreach (var childPath in childPaths.Values)
            {
                if (!FoldPartition(childPath, depth + 1, acceptWinner))
                    return false;
            }
            return true;
        }

        private static BinaryWriter NewWriter(string path) =>
            new(new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.SequentialScan), Encoding.UTF8, leaveOpen: false);

        private static BinaryReader NewReader(string path) =>
            new(new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan), Encoding.UTF8, leaveOpen: false);

        private static void WriteSample(BinaryWriter writer, FoldSample sample)
        {
            writer.Write(sample.GroupHash);
            writer.Write(sample.DedupHash ?? string.Empty);
            writer.Write(sample.TimestampMs.HasValue);
            if (sample.TimestampMs.HasValue)
                writer.Write(sample.TimestampMs.Value);
            writer.Write(sample.IsFinal);
            writer.Write(sample.Seq.HasValue);
            if (sample.Seq.HasValue)
                writer.Write(sample.Seq.Value);
            writer.Write(sample.InputTokens);
            writer.Write(sample.OutputTokens);
            writer.Write(sample.CacheReadTokens);
            writer.Write(sample.CacheCreationTokens);
        }

        private static bool TryReadSample(BinaryReader reader, out FoldSample sample)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
            {
                sample = null!;
                return false;
            }

            var groupHash = reader.ReadString();
            var dedupHash = reader.ReadString();
            long? timestamp = reader.ReadBoolean() ? reader.ReadInt64() : null;
            var isFinal = reader.ReadBoolean();
            long? seq = reader.ReadBoolean() ? reader.ReadInt64() : null;
            sample = new FoldSample(
                groupHash,
                string.IsNullOrEmpty(dedupHash) ? null : dedupHash,
                timestamp,
                isFinal,
                seq,
                reader.ReadInt64(),
                reader.ReadInt64(),
                reader.ReadInt64(),
                reader.ReadInt64());
            return true;
        }
    }

    /// <summary>
    /// One batch's bounded rollback record: the hashes it added to the global
    /// attribution index (≤ that many, and each slot it holds until
    /// rollback), the residual tokens it added per day (≤ one entry per day),
    /// and integer counter deltas. It stores hashes only — never paths,
    /// message ids or session content. Cache entries are independently staged
    /// by <see cref="IAgentUsageCacheBatch"/> until the batch is accepted.
    /// </summary>
    private sealed class BatchJournal
    {
        public List<string> AddedHashes { get; } = [];
        public Dictionary<string, long> ResidualDeltas { get; } = new(StringComparer.Ordinal);
        public int Matched { get; set; }
        public int Contributing { get; set; }
        public int BadLines { get; set; }
        public int SkippedFiles { get; set; }
        public int UnreadableFiles { get; set; }
    }

    private static Dictionary<string, long> FoldDayTotals(ScanState scan)
    {
        var totals = new Dictionary<string, long>(scan.ResidualDays, StringComparer.Ordinal);
        foreach (var record in scan.Attribution.Values)
            AddDay(totals, record.Day, record.Tokens);
        return totals;
    }

    private void EmitDays(IAgentUsageRecordSink sink, IReadOnlyDictionary<string, long> days)
    {
        // One synthetic record per day at local noon; the accumulator sums all
        // four token fields, so the whole day total rides on InputTokens.
        foreach (var (day, tokens) in days)
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
        int expected)
    {
        // Unavailable when the sessions tree is non-empty but literally
        // nothing usable exists: either no file could even be stat'ed, or the
        // extractor outage left zero cache hits. An empty tree stays
        // "available with zero data".
        var status = (counters.ScannedFiles == 0
                && (counters.SkippedFiles > 0 || counters.TraversalFailed))
            || (reasons.Contains(AgentUsageGapReason.ExtractorUnavailable)
                && counters.Contributing == 0
                && expected > 0)
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
            MatchedSessions: counters.Matched,
            ParserVersion,
            now,
            Detail: reasons.Count == 0 ? null : $"matched {counters.Matched} of {expected} sessions")
        {
            Reasons = AgentUsageGapReason.Ordered.Where(reasons.Contains).ToArray()
        };
    }

    private static string? DefaultHome()
    {
        var overridden = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".dsh");
    }

    private static string? DefaultFoldSpillRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, ".psx", "usage-cache", "v1", ".fold");
    }

    private sealed class NullDshExtractionEventSink : IDshExtractionEventSink
    {
        public static readonly NullDshExtractionEventSink Instance = new();

        public void OnHead(DshExtractedHead head)
        {
        }

        public void OnUsage(DshExtractedUsage usage)
        {
        }

        public void OnEof(DshExtractedEof eof)
        {
        }

        public void OnError(DshExtractedError error)
        {
        }
    }

    private sealed class ScanCounters
    {
        public int ScannedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int UnreadableFiles { get; set; }
        public int BadLines { get; set; }
        public int Matched { get; set; }
        public int Contributing { get; set; }
        public bool TraversalFailed { get; set; }
        public bool DiscoveryTruncated { get; set; }
    }

    private sealed class ScanState
    {
        public Dictionary<string, AgentUsageCacheRecord> Attribution { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ResidualDays { get; } = new(StringComparer.Ordinal);
        public bool LineageUnresolved { get; set; }
    }

    private readonly record struct PendingFile(string Path, long Size, long MtimeTicks);
}
