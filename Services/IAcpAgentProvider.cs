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

    /// <summary>
    /// Optional exact-usage data source for the global Usage panel. Providers
    /// that can attribute precise token counts to PSX sessions (e.g. Claude
    /// Code's session JSONL) return one here; others return null and are shown
    /// with context snapshots only. This is data-driven capability, never a
    /// provider-name branch in shared code.
    /// </summary>
    IAgentUsageSource? UsageSource => null;

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
