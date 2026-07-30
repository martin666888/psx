using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Builds the global Usage report: local activity from the thread store plus
/// per-provider exact usage from each provider's <see cref="IAgentUsageSource"/>.
/// Aggregation is provider-agnostic (no provider-name branches) and windowed
/// server-side so the frontend never has to reconstruct de-duplicated window
/// totals from the daily series.
///
/// Collection is single-flight with a short TTL: concurrent callers await the
/// same in-flight scan; a forced refresh bypasses the cache and coalesces
/// concurrent forced callers onto one scan.
/// </summary>
public sealed class AgentUsageService
{
    public const int HeatmapDays = 365;
    public const string PsxThreadsSourceKey = "psx-threads";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IAgentThreadStore _threadStore;
    private readonly IAgentProviderRegistry _providerRegistry;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private Task<AgentUsageResult>? _inFlight;
    private bool _inFlightIsForced;
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
            _inFlight = Task.Run(() => Collect(cancellationToken), cancellationToken);
            var task = _inFlight;
            _ = task.ContinueWith(completed =>
            {
                lock (_gate)
                {
                    if (ReferenceEqualityComparer.Instance.Equals(_inFlight, completed))
                        _inFlight = null;
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
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
        var sources = new List<AgentUsageSourceStatus>
        {
            BuildPsxThreadsStatus(snapshot)
        };

        // Per provider: exact-usage records (windowed later) + current context
        // snapshots. Everything is keyed by provider registry key; the frontend
        // renders sections purely by which fields are populated.
        var providerRecords = new Dictionary<string, IReadOnlyList<AgentUsageRecord>>(StringComparer.OrdinalIgnoreCase);
        var providerContext = new Dictionary<string, IReadOnlyList<AgentUsageContextSnapshot>>(StringComparer.OrdinalIgnoreCase);
        var providerIconKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var providerSourceKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providerRegistry.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = provider.Descriptor.Key;
            providerIconKey[key] = provider.Descriptor.IconKey;

            var threads = snapshot.Threads
                .Where(thread => ReferenceEquals(_providerRegistry.Find(thread.Provider), provider))
                .ToArray();

            var context = threads
                .Where(thread => thread.ContextUsedTokens.HasValue)
                .Select(thread => new AgentUsageContextSnapshot(
                    thread.Title, thread.ContextUsedTokens, thread.ContextWindowTokens))
                .ToArray();
            if (context.Length > 0)
                providerContext[key] = context;

            if (provider.UsageSource != null)
            {
                var sessionIds = threads
                    .SelectMany(thread => new[] { thread.ClaudeSessionId, thread.AcpSessionId })
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var contribution = provider.UsageSource.Collect(sessionIds, cancellationToken);
                providerRecords[key] = contribution.Records;
                sources.Add(contribution.Status);
                providerSourceKey[key] = contribution.Status.Key;
            }
            else
            {
                // Context-only sections attribute to the local thread store.
                providerSourceKey[key] = PsxThreadsSourceKey;
            }
        }

        var report = new AgentUsageReport(
            BuildHeatmap(snapshot, heatmapStart, today, localZone),
            heatmapStart,
            BuildWindow(snapshot, providerRecords, providerContext, providerIconKey, providerSourceKey,
                today, today, localZone),
            BuildWindow(snapshot, providerRecords, providerContext, providerIconKey, providerSourceKey,
                today.AddDays(-6), today, localZone),
            BuildWindow(snapshot, providerRecords, providerContext, providerIconKey, providerSourceKey,
                today.AddDays(-29), today, localZone));

        return new AgentUsageResult(
            _timeProvider.GetUtcNow(), localZone.Id, report, sources);
    }

    private static AgentUsageSourceStatus BuildPsxThreadsStatus(AgentThreadUsageSnapshot snapshot)
    {
        var partial = snapshot.SkippedFiles > 0;
        return new AgentUsageSourceStatus(
            PsxThreadsSourceKey,
            partial ? AgentUsageSourceStatus.Partial : AgentUsageSourceStatus.Available,
            snapshot.ScannedFiles,
            snapshot.SkippedFiles,
            BadLines: 0,
            ExpectedSessions: null,
            MatchedSessions: null,
            ParserVersion: "1",
            LastScanAt: DateTimeOffset.Now,
            Detail: partial
                ? $"Partial data: skipped {snapshot.SkippedFiles} unreadable thread file(s)."
                : null);
    }

    private static IReadOnlyList<int> BuildHeatmap(
        AgentThreadUsageSnapshot snapshot, DateOnly start, DateOnly today, TimeZoneInfo zone)
    {
        var counts = new int[HeatmapDays];
        foreach (var thread in snapshot.Threads)
        {
            var activeDays = thread.Messages
                .Where(message => message.Role == "user")
                .Select(message => DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(message.CreatedAt, zone).DateTime))
                .Where(date => date >= start && date <= today)
                .Distinct();
            foreach (var date in activeDays)
            {
                var index = date.DayNumber - start.DayNumber;
                if (index >= 0 && index < HeatmapDays)
                    counts[index]++;
            }
        }

        return counts;
    }

    private static AgentUsageWindow BuildWindow(
        AgentThreadUsageSnapshot snapshot,
        Dictionary<string, IReadOnlyList<AgentUsageRecord>> providerRecords,
        Dictionary<string, IReadOnlyList<AgentUsageContextSnapshot>> providerContext,
        Dictionary<string, string> providerIconKey,
        Dictionary<string, string> providerSourceKey,
        DateOnly start,
        DateOnly today,
        TimeZoneInfo zone)
    {
        var activeThreads = 0;
        var turns = 0;
        foreach (var thread in snapshot.Threads)
        {
            var userInWindow = thread.Messages
                .Where(message => message.Role == "user")
                .Count(message =>
                {
                    var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(message.CreatedAt, zone).DateTime);
                    return date >= start && date <= today;
                });
            if (userInWindow > 0)
            {
                activeThreads++;
                turns += userInWindow;
            }
        }

        var sections = new List<AgentUsageProviderSection>();
        long totalInput = 0, totalOutput = 0, totalCacheRead = 0, totalCacheCreation = 0;

        var providerKeys = providerRecords.Keys
            .Concat(providerContext.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var key in providerKeys)
        {
            AgentUsageExactUsage? exactUsage = null;
            if (providerRecords.TryGetValue(key, out var records))
            {
                var rows = records
                    .Where(record =>
                    {
                        var date = DateOnly.FromDateTime(
                            TimeZoneInfo.ConvertTime(record.Timestamp, zone).DateTime);
                        return date >= start && date <= today;
                    })
                    .GroupBy(record => record.Model, StringComparer.Ordinal)
                    .Select(group => new AgentUsageModelRow(
                        group.Key,
                        group.Sum(record => record.InputTokens),
                        group.Sum(record => record.OutputTokens),
                        group.Sum(record => record.CacheReadTokens),
                        group.Sum(record => record.CacheCreationTokens)))
                    .OrderByDescending(row => row.Input + row.Output + row.CacheRead + row.CacheCreation)
                    .ToArray();

                if (rows.Length > 0)
                {
                    exactUsage = new AgentUsageExactUsage(rows);
                    foreach (var row in rows)
                    {
                        totalInput += row.Input;
                        totalOutput += row.Output;
                        totalCacheRead += row.CacheRead;
                        totalCacheCreation += row.CacheCreation;
                    }
                }
            }

            providerContext.TryGetValue(key, out var context);

            if (exactUsage == null && (context == null || context.Count == 0))
                continue;

            sections.Add(new AgentUsageProviderSection(
                key,
                providerIconKey.TryGetValue(key, out var icon) ? icon : "agent",
                exactUsage,
                context,
                providerSourceKey.TryGetValue(key, out var source) ? source : PsxThreadsSourceKey));
        }

        var cacheableInput = totalInput + totalCacheRead + totalCacheCreation;
        double? cacheHitRate = cacheableInput > 0 ? (double)totalCacheRead / cacheableInput : null;

        return new AgentUsageWindow(
            activeThreads,
            turns,
            new AgentUsageTokens(totalInput, totalOutput, totalCacheRead, totalCacheCreation),
            cacheHitRate,
            sections);
    }
}
