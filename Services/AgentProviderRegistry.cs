using PSX.Models;

namespace PSX.Services;

public sealed class AgentProviderRegistry : IAgentProviderRegistry
{
    private readonly IReadOnlyList<IAcpAgentProvider> _providers;
    private readonly Dictionary<string, IAcpAgentProvider> _providersByKey;

    public AgentProviderRegistry(
        IEnumerable<IAcpAgentProvider> providers,
        AgentProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);

        _providers = providers.ToArray();
        if (_providers.Count == 0)
            throw new InvalidOperationException("At least one ACP Agent provider must be registered.");

        _providersByKey = new Dictionary<string, IAcpAgentProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            RegisterKey(provider.Descriptor.Key, provider, "provider");
            foreach (var legacyKey in provider.Descriptor.LegacyKeys)
            {
                if (!string.IsNullOrWhiteSpace(legacyKey))
                    RegisterKey(legacyKey, provider, "legacy provider");
            }
        }

        DefaultProvider = Find(options.DefaultProviderKey)
            ?? throw new InvalidOperationException(
                $"The default ACP Agent provider '{options.DefaultProviderKey}' is not registered.");
    }

    private void RegisterKey(string key, IAcpAgentProvider provider, string keyKind)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"ACP Agent {keyKind} keys must not be empty.");

        if (_providersByKey.TryGetValue(key, out var existing))
        {
            throw new InvalidOperationException(
                $"ACP Agent provider key '{key}' is registered by both " +
                $"'{existing.Descriptor.DisplayName}' and '{provider.Descriptor.DisplayName}'.");
        }

        _providersByKey.Add(key, provider);
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
