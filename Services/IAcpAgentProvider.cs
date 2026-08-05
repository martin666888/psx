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
    /// Code's session JSONL) return one here; threads for providers without a
    /// source contribute to the folded completeness gap instead of receiving
    /// an estimated total. This is data-driven capability, never a
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

    /// <summary>
    /// Optional interactive login terminal. When non-null, the session engine
    /// opens this profile instead of appending <c>--login</c> to the ACP
    /// process spec (which is wrong for CLIs whose login entry is a separate
    /// subcommand such as <c>qodercli login</c>, or an interactive TUI such as
    /// Qwen Code rather than <c>--acp --login</c>).
    /// </summary>
    ShellProfile? CreateLoginTerminalProfile(string workingDirectory) => null;

    /// <summary>
    /// Budget for the first <c>session/new</c> (and equivalent) RPC when
    /// establishing a live session. Providers whose unauthenticated startup is
    /// known to be slow may raise this above the default 30s.
    /// </summary>
    TimeSpan NewSessionTimeout => TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, a timed-out first <c>session/new</c> is treated as a
    /// recoverable auth failure: open the login terminal and stay in
    /// <c>auth_required</c> instead of a hard error. Used by providers whose
    /// unauthenticated <c>session/new</c> can hang until the user logs in.
    /// </summary>
    bool TreatNewSessionTimeoutAsAuthRequired => false;
}

public interface IAgentProviderRegistry
{
    IAcpAgentProvider DefaultProvider { get; }
    IReadOnlyList<IAcpAgentProvider> Providers { get; }
    IAcpAgentProvider? Find(string? providerKey);
}
