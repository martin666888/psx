using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Builds the global Usage report from provider exact-usage sources. Aggregation
/// is provider-agnostic (no provider-name branches); records stream directly
/// into bounded per-provider day arrays.
///
/// Collection is single-flight with a short TTL: concurrent callers await the
/// same in-flight scan; a forced refresh bypasses the cache and coalesces
/// concurrent forced callers onto one scan.
/// </summary>
public sealed class AgentUsageService
{
    public const int HeatmapDays = 365;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentProviderRegistry _providerRegistry;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private Task<AgentUsageResult>? _inFlight;
    private bool _inFlightIsForced;
    private long _scanGeneration;
    private long _publishedGeneration;
    private AgentUsageResult? _cached;
    private DateTimeOffset _cachedAt;

    public AgentUsageService(
        IAgentThreadStore threadStore,
        IAgentProviderRegistry providerRegistry,
        TimeProvider? timeProvider = null)
    {
        _threadStore = threadStore;
        _providerRegistry = providerRegistry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AgentUsageResult> CollectAsync(bool force, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!force
                && _cached != null
                && _timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
            {
                return Task.FromResult(_cached);
            }

            // Reuse an in-flight scan. A forced request only reuses another
            // forced scan; a forced request while a cached (non-forced) scan is
            // running starts its own so it truly re-reads from disk.
            if (_inFlight != null && (!force || _inFlightIsForced))
                return _inFlight;

            _inFlightIsForced = force;
            var generation = ++_scanGeneration;
            _inFlight = Task.Run(() => Collect(cancellationToken), cancellationToken);
            var task = _inFlight;
            _ = task.ContinueWith(completed =>
            {
                lock (_gate)
                {
                    if (ReferenceEqualityComparer.Instance.Equals(_inFlight, completed))
                        _inFlight = null;
                    // Only the newest completed scan may publish. An older
                    // non-forced scan that finishes after a forced refresh must
                    // not overwrite fresher cache contents.
                    if (completed.Status == TaskStatus.RanToCompletion
                        && generation >= _publishedGeneration)
                    {
                        _publishedGeneration = generation;
                        _cached = completed.Result;
                        _cachedAt = _timeProvider.GetUtcNow();
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private AgentUsageResult Collect(CancellationToken cancellationToken)
    {
        var localZone = _timeProvider.LocalTimeZone;
        var nowLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), localZone);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var heatmapStart = today.AddDays(-(HeatmapDays - 1));

        var snapshot = _threadStore.ReadUsageSnapshot();
        var providerResults = new List<ProviderCollectionResult>();
        var trackedThreadCount = 0;

        foreach (var provider in _providerRegistry.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var threads = snapshot.Threads
                .Where(thread => ReferenceEquals(_providerRegistry.Find(thread.Provider), provider))
                .ToArray();
            trackedThreadCount += threads.Length;

            if (provider.UsageSource == null)
            {
                if (threads.Length > 0)
                {
                    var reasons = new HashSet<string>(StringComparer.Ordinal)
                    {
                        AgentUsageGapReason.UnsupportedSource
                    };
                    if (threads.Any(thread =>
                            string.IsNullOrWhiteSpace(thread.ClaudeSessionId)
                            && string.IsNullOrWhiteSpace(thread.AcpSessionId)))
                    {
                        reasons.Add(AgentUsageGapReason.MissingSessionId);
                    }

                    var completeness = new AgentUsageCompleteness(
                        AgentUsageCompleteness.Unavailable,
                        OrderReasons(reasons),
                        ExpectedSessions: null,
                        MatchedSessions: null,
                        SkippedFiles: 0,
                        BadLines: 0,
                        UntrackedThreads: threads.Length);
                    providerResults.Add(new ProviderCollectionResult(
                        BuildProviderReport(
                            provider, new long[HeatmapDays], completeness),
                        threads.Length));
                }
                continue;
            }

            var sessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingSessionIds = 0;
            foreach (var thread in threads)
            {
                var sessionId = provider.UsageSource.ResolveSessionId(thread);
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    missingSessionIds++;
                    continue;
                }

                sessionIds.Add(sessionId);
            }

            var accumulator = new DailyUsageAccumulator(
                heatmapStart, today, localZone);
            var sourceStatus = provider.UsageSource.Collect(
                sessionIds, accumulator, cancellationToken);
            var providerCompleteness = BuildProviderCompleteness(
                threads.Length, missingSessionIds, sourceStatus);
            providerResults.Add(new ProviderCollectionResult(
                BuildProviderReport(
                    provider, accumulator.DailyTokens, providerCompleteness),
                threads.Length));
        }

        var unknownThreads = snapshot.Threads.Count - trackedThreadCount;
        var dailyTokens = new long[HeatmapDays];
        foreach (var providerResult in providerResults)
            AddSeries(dailyTokens, providerResult.Report.DailyTokens);
        var report = new AgentUsageReport(
            heatmapStart,
            dailyTokens,
            new AgentUsageWindow(SumTail(dailyTokens, 1)),
            new AgentUsageWindow(SumTail(dailyTokens, 7)),
            new AgentUsageWindow(SumTail(dailyTokens, 30)),
            providerResults.Select(result => result.Report).ToArray());

        return new AgentUsageResult(
            _timeProvider.GetUtcNow(),
            localZone.Id,
            report,
            BuildOverallCompleteness(snapshot, providerResults, unknownThreads));
    }

    private static AgentProviderUsageReport BuildProviderReport(
        IAcpAgentProvider provider,
        IReadOnlyList<long> dailyTokens,
        AgentUsageCompleteness completeness)
    {
        return new AgentProviderUsageReport(
            provider.Descriptor.Key,
            provider.Descriptor.DisplayName,
            provider.Descriptor.IconKey,
            dailyTokens,
            new AgentUsageWindow(SumTail(dailyTokens, 1)),
            new AgentUsageWindow(SumTail(dailyTokens, 7)),
            new AgentUsageWindow(SumTail(dailyTokens, 30)),
            completeness);
    }

    private static long SumTail(IReadOnlyList<long> values, int count)
    {
        long total = 0;
        var start = Math.Max(0, values.Count - count);
        for (var index = start; index < values.Count; index++)
            total = checked(total + values[index]);
        return total;
    }

    private static AgentUsageCompleteness BuildProviderCompleteness(
        int threadCount,
        int missingSessionIds,
        AgentUsageSourceStatus source)
    {
        var reasons = new HashSet<string>(source.Reasons, StringComparer.Ordinal);
        if (missingSessionIds > 0)
            reasons.Add(AgentUsageGapReason.MissingSessionId);
        if (source.ExpectedSessions.HasValue
            && source.MatchedSessions.HasValue
            && source.MatchedSessions.Value < source.ExpectedSessions.Value)
        {
            reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        }

        var hasGap = reasons.Count > 0
            || source.Status != AgentUsageSourceStatus.Available
            || source.SkippedFiles > 0
            || source.BadLines > 0
            || missingSessionIds > 0;
        var matched = source.MatchedSessions ?? 0;
        var status = threadCount == 0
            ? AgentUsageCompleteness.Available
            : matched == 0
                && ((source.ExpectedSessions ?? 0) > 0
                    || missingSessionIds == threadCount
                    || source.Status == AgentUsageSourceStatus.Unavailable)
                ? AgentUsageCompleteness.Unavailable
                : hasGap
                    ? AgentUsageCompleteness.Partial
                    : AgentUsageCompleteness.Available;

        return new AgentUsageCompleteness(
            status,
            OrderReasons(reasons),
            source.ExpectedSessions,
            source.MatchedSessions,
            source.SkippedFiles,
            source.BadLines,
            missingSessionIds);
    }

    private static AgentUsageCompleteness BuildOverallCompleteness(
        AgentThreadUsageSnapshot snapshot,
        IReadOnlyList<ProviderCollectionResult> providerResults,
        int unknownThreads)
    {
        int? expectedSessions = null;
        int? matchedSessions = null;
        var applicable = providerResults
            .Where(result => result.ThreadCount > 0)
            .Select(result => result.Report.Completeness)
            .ToArray();
        if (applicable.All(completeness =>
                completeness.ExpectedSessions.HasValue
                && completeness.MatchedSessions.HasValue))
        {
            expectedSessions = applicable.Sum(
                completeness => completeness.ExpectedSessions!.Value);
            matchedSessions = applicable.Sum(
                completeness => completeness.MatchedSessions!.Value);
        }

        var untrackedThreads = checked(
            unknownThreads
            + providerResults.Sum(result =>
                result.Report.Completeness.UntrackedThreads));
        var skippedFiles = checked(
            snapshot.SkippedFiles
            + providerResults.Sum(result =>
                result.Report.Completeness.SkippedFiles));
        var badLines = providerResults.Sum(result =>
            result.Report.Completeness.BadLines);
        var reasons = new HashSet<string>(
            providerResults.SelectMany(result =>
                result.Report.Completeness.Reasons),
            StringComparer.Ordinal);
        if (snapshot.SkippedFiles > 0)
            reasons.Add(AgentUsageGapReason.DamagedThreadFiles);
        if (unknownThreads > 0)
            reasons.Add(AgentUsageGapReason.UnregisteredProvider);

        var hasGap = skippedFiles > 0
            || badLines > 0
            || untrackedThreads > 0
            || providerResults.Any(result =>
                result.Report.Completeness.Status != AgentUsageCompleteness.Available)
            || reasons.Count > 0;
        var matchedForAvailability = providerResults.Sum(result =>
            result.Report.Completeness.MatchedSessions ?? 0);
        var status = snapshot.Threads.Count > 0 && matchedForAvailability == 0
            ? AgentUsageCompleteness.Unavailable
            : hasGap
                ? AgentUsageCompleteness.Partial
                : AgentUsageCompleteness.Available;

        return new AgentUsageCompleteness(
            status,
            OrderReasons(reasons),
            expectedSessions,
            matchedSessions,
            skippedFiles,
            badLines,
            untrackedThreads);
    }

    private static void AddSeries(long[] destination, IReadOnlyList<long> source)
    {
        for (var index = 0; index < Math.Min(destination.Length, source.Count); index++)
            destination[index] = checked(destination[index] + source[index]);
    }

    private static IReadOnlyList<string> OrderReasons(IEnumerable<string> reasons)
    {
        var set = new HashSet<string>(reasons, StringComparer.Ordinal);
        return AgentUsageGapReason.Ordered.Where(set.Contains).ToArray();
    }

    private sealed record ProviderCollectionResult(
        AgentProviderUsageReport Report,
        int ThreadCount);

    private sealed class DailyUsageAccumulator(
        DateOnly start,
        DateOnly today,
        TimeZoneInfo zone) : IAgentUsageRecordSink
    {
        public long[] DailyTokens { get; } = new long[HeatmapDays];

        public void Add(AgentUsageRecord record)
        {
            var date = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(record.Timestamp, zone).DateTime);
            if (date < start || date > today)
                return;

            var index = date.DayNumber - start.DayNumber;
            DailyTokens[index] = checked(
                DailyTokens[index]
                + record.InputTokens
                + record.OutputTokens
                + record.CacheReadTokens
                + record.CacheCreationTokens);
        }
    }
}
