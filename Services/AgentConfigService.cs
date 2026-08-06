using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Builds the global Config report from provider config sources. Aggregation
/// is provider-agnostic (no provider-name branches). Collection is
/// single-flight with a short TTL; a forced refresh bypasses the cache and
/// coalesces concurrent forced callers onto one scan.
/// </summary>
public sealed class AgentConfigService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IAgentProviderRegistry _providerRegistry;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private Task<AgentConfigResult>? _inFlight;
    private bool _inFlightIsForced;
    private long _scanGeneration;
    private long _publishedGeneration;
    private AgentConfigResult? _cached;
    private DateTimeOffset _cachedAt;

    public AgentConfigService(
        IAgentProviderRegistry providerRegistry,
        TimeProvider? timeProvider = null)
    {
        _providerRegistry = providerRegistry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AgentConfigResult> CollectAsync(bool force, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!force
                && _cached != null
                && _timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
            {
                return Task.FromResult(_cached);
            }

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

    private AgentConfigResult Collect(CancellationToken cancellationToken)
    {
        var providers = new List<AgentProviderConfigReport>();

        foreach (var provider in _providerRegistry.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (provider.ConfigSource == null)
            {
                // Every production provider registers a ConfigSource; leave a
                // defensive empty slot only if a future provider omits one.
                continue;
            }

            AgentProviderConfigReport report;
            try
            {
                report = provider.ConfigSource.Collect(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Agent config scan failed for {provider.Descriptor.Key}: {ex}");
                report = new AgentProviderConfigReport(
                    provider.Descriptor.Key,
                    provider.Descriptor.DisplayName,
                    provider.Descriptor.IconKey,
                    AgentProviderConfigReport.Unavailable,
                    [],
                    [],
                    [],
                    [],
                    [AgentConfigNotes.ParseFailed]);
            }

            // Sources leave identity blank; the aggregator stamps the
            // descriptor so shared code stays provider-name free.
            providers.Add(report with
            {
                ProviderKey = provider.Descriptor.Key,
                DisplayName = provider.Descriptor.DisplayName,
                IconKey = provider.Descriptor.IconKey
            });
        }

        return new AgentConfigResult(
            _timeProvider.GetUtcNow(),
            new AgentConfigReport(providers));
    }
}
