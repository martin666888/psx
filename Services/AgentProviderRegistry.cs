namespace PSX.Services;

public sealed class AgentProviderRegistry : IAgentProviderRegistry
{
    private readonly IReadOnlyList<IAcpAgentProvider> _providers;
    private readonly Dictionary<string, IAcpAgentProvider> _providersByKey;

    public AgentProviderRegistry(ClaudeAcpAgentProvider defaultProvider)
    {
        DefaultProvider = defaultProvider;
        _providers = new[] { defaultProvider };
        _providersByKey = new Dictionary<string, IAcpAgentProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            _providersByKey[provider.Descriptor.Key] = provider;
            foreach (var legacyKey in provider.Descriptor.LegacyKeys)
            {
                if (!string.IsNullOrWhiteSpace(legacyKey))
                    _providersByKey[legacyKey] = provider;
            }
        }
    }

    public IAcpAgentProvider DefaultProvider { get; }

    public IReadOnlyList<IAcpAgentProvider> Providers => _providers;

    public IAcpAgentProvider? Find(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return null;

        return _providersByKey.TryGetValue(providerKey, out var provider) ? provider : null;
    }
}
