using PSX.Models;

namespace PSX.Services;

public interface IAcpAgentProvider
{
    AgentDescriptor Descriptor { get; }
    IAcpAgentRuntime Runtime { get; }

    /// <summary>
    /// ACP client capabilities PSX advertises for this provider on
    /// <c>initialize</c>. Provider-specific capability policy (e.g. Kimi opting
    /// out of the reverse <c>fs</c> bridge) is expressed here.
    /// </summary>
    AcpClientCapabilityProfile ClientCapabilities { get; }

    object CreateNewSessionParameters(string workingDirectory);

    object CreateLoadSessionParameters(string sessionId, string workingDirectory);

    bool IsCommandVisible(string normalizedCommand);

    ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId);
}

public interface IAgentProviderRegistry
{
    IAcpAgentProvider DefaultProvider { get; }
    IReadOnlyList<IAcpAgentProvider> Providers { get; }
    IAcpAgentProvider? Find(string? providerKey);
}
