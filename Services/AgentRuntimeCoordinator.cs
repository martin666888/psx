namespace PSX.Services;

public interface IAgentRuntimeCoordinator
{
    Task PrepareForStartupAsync(CancellationToken cancellationToken = default);
    Task RefreshReadyAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentRuntimeCoordinator : IAgentRuntimeCoordinator, IDisposable
{
    private readonly IReadOnlyList<IAcpAgentRuntime> _runtimes;

    public AgentRuntimeCoordinator(IAgentProviderRegistry providers)
    {
        _runtimes = providers.Providers
            .Select(provider => provider.Runtime)
            .Distinct<IAcpAgentRuntime>(ReferenceEqualityComparer.Instance)
            .ToArray();
    }

    public async Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _runtimes)
            await runtime.PrepareForStartupAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshReadyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in _runtimes)
        {
            if (runtime.IsReady())
                await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() { }
}
