using PSX.Models;

namespace PSX.Services;

public interface IAcpAgentProvider
{
    AgentDescriptor Descriptor { get; }
    IAcpAgentRuntime Runtime { get; }

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
