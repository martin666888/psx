using PSX.Models;

namespace PSX.Services;

/// <summary>
/// ACP provider for the external Qoder CLI. PSX discovers and launches
/// <c>qodercli --acp</c> from PATH but does not install or update the CLI.
/// </summary>
public sealed class QoderCliAcpAgentProvider : IAcpAgentProvider
{
    private readonly QoderCliAcpRuntime _runtime;

    public QoderCliAcpAgentProvider(QoderCliAcpRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Runtime = runtime;
    }

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-qoder",
        DisplayName: "Qoder CLI",
        AssistantName: "Qoder",
        LegacyKeys: Array.Empty<string>())
    {
        IconKey = "qoder"
    };

    public IAcpAgentRuntime Runtime { get; }

    public IAgentUsageSource? UsageSource { get; } = null;

    // Phase 0: unauthenticated session/new can exceed 45s. Give headroom and
    // map that stall onto auth_required + qodercli login instead of a hard error.
    public TimeSpan NewSessionTimeout => TimeSpan.FromSeconds(90);

    public bool TreatNewSessionTimeoutAsAuthRequired => true;

    public AcpClientCapabilityProfile ClientCapabilities { get; } = new()
    {
        FileSystemReadText = true,
        FileSystemWriteText = true,
        Terminal = true,
        SessionBooleanConfig = true,
        ElicitationFormUrl = true,
        TerminalOutputMeta = true
    };

    public object CreateNewSessionParameters(string workingDirectory)
    {
        return new
        {
            cwd = workingDirectory,
            mcpServers = Array.Empty<object>()
        };
    }

    public object CreateRestoreSessionParameters(string sessionId, string workingDirectory)
    {
        return new
        {
            sessionId,
            cwd = workingDirectory,
            mcpServers = Array.Empty<object>()
        };
    }

    public bool IsCommandVisible(string normalizedCommand) => true;

    /// <summary>
    /// Only used for the explicit <c>/terminal</c> entry point and troubleshooting,
    /// never as a normal chat path. Prefer the resolved qodercli path so a PATH
    /// that only finds the AppData npm shim still works.
    /// </summary>
    public ShellProfile CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var escapedCwd = workingDirectory.Replace("'", "''");
        var entry = QuoteForPowerShell(_runtime.TryGetDiscoveredEntryPath() ?? "qodercli");
        return new ShellProfile
        {
            Id = "qoder-cli",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; & {entry}",
            StartingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Qoder documents interactive login as <c>qodercli login</c>, not
    /// <c>qodercli --acp --login</c>.
    /// </summary>
    public ShellProfile CreateLoginTerminalProfile(string workingDirectory)
    {
        var escapedCwd = workingDirectory.Replace("'", "''");
        var entry = QuoteForPowerShell(_runtime.TryGetDiscoveredEntryPath() ?? "qodercli");
        return new ShellProfile
        {
            Id = "qoder-cli-login",
            Name = $"{Descriptor.DisplayName} 登录",
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; & {entry} login",
            StartingDirectory = workingDirectory
        };
    }

    private static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";
}
