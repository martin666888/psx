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

    /// <summary>
    /// Parameters for restoring an existing agent-side session. ACP gives
    /// <c>session/load</c> and <c>session/resume</c> the same shape
    /// (<c>sessionId</c>, <c>cwd</c>, <c>mcpServers</c>), so one factory
    /// serves both restore paths.
    /// </summary>
    object CreateRestoreSessionParameters(string sessionId, string workingDirectory);

    bool IsCommandVisible(string normalizedCommand);

    ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId);
}

public interface IAgentProviderRegistry
{
    IAcpAgentProvider DefaultProvider { get; }
    IReadOnlyList<IAcpAgentProvider> Providers { get; }
    IAcpAgentProvider? Find(string? providerKey);
}
