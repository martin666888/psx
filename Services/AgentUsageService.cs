using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Builds the global Usage report from provider exact-usage sources and
/// local-machine usage contributors. Aggregation is provider-agnostic (no
/// provider-name branches); records stream directly into bounded per-source
/// day arrays.
///
/// Collection is strictly serialized: at most one scan runs at any time, so
/// disk-cache writes never race. Callers that arrive while a scan is in
/// flight await it; a forced request is satisfied only by a scan that started
/// after the request was made, so a force always re-reads from disk. A short
/// TTL lets non-forced callers reuse the latest published result.
/// </summary>
public sealed class AgentUsageService
{
    public const int HeatmapDays = 365;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentProviderRegistry _providerRegistry;
    private readonly IReadOnlyList<IAgentLocalUsageContributor> _localContributors;
    private readonly HashSet<string> _claimedProviderKeys;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private Task<AgentUsageResult>? _scanTail;
    private long _clock;
    private long _scanStartMark;
    private AgentUsageResult? _cached;
    private DateTimeOffset _cachedAt;

    public AgentUsageService(
        IAgentThreadStore threadStore,
        IAgentProviderRegistry providerRegistry,
        TimeProvider? timeProvider = null,
        IEnumerable<IAgentLocalUsageContributor>? localContributors = null)
    {
        _threadStore = threadStore;
        _providerRegistry = providerRegistry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localContributors = localContributors?.ToArray() ?? [];
        _claimedProviderKeys = new HashSet<string>(
            _localContributors
                .Select(contributor => contributor.Descriptor.TrackedProviderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))!,
            StringComparer.Ordinal);
    }

    public Task<AgentUsageResult> CollectAsync(bool force, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!force && IsFreshLocked())
                return Task.FromResult(_cached!);

            var requestMark = ++_clock;
            if (_scanTail == null || _scanTail.IsCompleted)
                return StartScanLocked(cancellationToken);

            return WaitForScanAsync(force, requestMark, cancellationToken);
        }
    }

    private async Task<AgentUsageResult> WaitForScanAsync(
        bool force,
        long requestMark,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<AgentUsageResult> tail;
            lock (_gate)
            {
                tail = _scanTail!;
            }

            try
            {
                await tail.ConfigureAwait(false);
            }
            catch
            {
                // Scan failures propagate below; waiting is over either way.
            }

            Task<AgentUsageResult>? next = null;
            var propagateFault = false;
            lock (_gate)
            {
                if (IsFreshLocked() && (!force || _scanStartMark >= requestMark))
                    return _cached!;

                if (tail.IsFaulted && ReferenceEquals(_scanTail, tail))
                {
                    propagateFault = true;
                }
                else if (_scanTail == null || _scanTail.IsCompleted)
                {
                    next = StartScanLocked(cancellationToken);
                }

                // A newer scan is in flight; loop and await it.
            }

            if (propagateFault)
                return await tail.ConfigureAwait(false);
            if (next != null)
                return await next.ConfigureAwait(false);
        }
    }

    private Task<AgentUsageResult> StartScanLocked(CancellationToken cancellationToken)
    {
        var scan = Task.Run(() => Collect(cancellationToken), cancellationToken);
        _scanStartMark = _clock;
        _scanTail = scan;
        _ = scan.ContinueWith(completed =>
        {
            lock (_gate)
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    _cached = completed.Result;
                    _cachedAt = _timeProvider.GetUtcNow();
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return scan;
    }

    private bool IsFreshLocked() =>
        _cached != null && _timeProvider.GetUtcNow() - _cachedAt < CacheTtl;

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

            // A local contributor claiming this provider owns its usage view;
            // the threads count as tracked without a second attribution.
            if (_claimedProviderKeys.Contains(provider.Descriptor.Key))
            {
                trackedThreadCount += threads.Length;
                continue;
            }

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
                        threads.Length,
                        AlwaysInclude: false));
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
                threads.Length,
                AlwaysInclude: false));
        }

        // Local-machine contributors always appear in the report, even with
        // zero data, so users can see the source is active.
        foreach (var contributor in _localContributors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accumulator = new DailyUsageAccumulator(heatmapStart, today, localZone);
            var status = contributor.Collect(accumulator, cancellationToken);
            providerResults.Add(new ProviderCollectionResult(
                BuildLocalContributorReport(contributor, accumulator.DailyTokens, status),
                ThreadCount: 0,
                AlwaysInclude: true));
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
            completeness,
            AgentUsageScope.PsxSessions);
    }

    private static AgentProviderUsageReport BuildLocalContributorReport(
        IAgentLocalUsageContributor contributor,
        IReadOnlyList<long> dailyTokens,
        AgentUsageSourceStatus status)
    {
        var reasons = new HashSet<string>(status.Reasons, StringComparer.Ordinal);
        if (status.ExpectedSessions.HasValue
            && status.MatchedSessions.HasValue
            && status.MatchedSessions.Value < status.ExpectedSessions.Value)
        {
            reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        }

        var hasGap = reasons.Count > 0
            || status.Status != AgentUsageSourceStatus.Available
            || status.SkippedFiles > 0
            || status.BadLines > 0;
        var completenessStatus = status.Status == AgentUsageSourceStatus.Unavailable
            ? AgentUsageCompleteness.Unavailable
            : hasGap
                ? AgentUsageCompleteness.Partial
                : AgentUsageCompleteness.Available;

        var completeness = new AgentUsageCompleteness(
            completenessStatus,
            OrderReasons(reasons),
            status.ExpectedSessions,
            status.MatchedSessions,
            status.SkippedFiles,
            status.BadLines,
            UntrackedThreads: 0);
        return new AgentProviderUsageReport(
            contributor.Descriptor.Key,
            contributor.Descriptor.DisplayName,
            contributor.Descriptor.IconKey,
            dailyTokens,
            new AgentUsageWindow(SumTail(dailyTokens, 1)),
            new AgentUsageWindow(SumTail(dailyTokens, 7)),
            new AgentUsageWindow(SumTail(dailyTokens, 30)),
            completeness,
            AgentUsageScope.LocalAll);
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
            .Where(result => result.ThreadCount > 0 || result.AlwaysInclude)
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
        // Nothing matched only degrades the whole report when some source
        // actually failed or expected data; a healthy local contributor that
        // simply found no session logs keeps the report available at zero.
        var anyUnfulfilledExpectation = applicable.Any(completeness =>
            completeness.Status == AgentUsageCompleteness.Unavailable
            || (completeness.ExpectedSessions ?? 0) > 0
            || completeness.UntrackedThreads > 0);
        // Terminal-only startup has no threads at all: the report is still
        // unavailable when every applicable source failed, so a fully broken
        // local scan does not masquerade as "available with zero data".
        var allApplicableUnavailable = applicable.Length > 0
            && applicable.All(completeness =>
                completeness.Status == AgentUsageCompleteness.Unavailable);
        var status = (snapshot.Threads.Count > 0
                && matchedForAvailability == 0
                && anyUnfulfilledExpectation)
            || (matchedForAvailability == 0 && allApplicableUnavailable)
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
        int ThreadCount,
        bool AlwaysInclude);

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
