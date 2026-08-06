using PSX.Models;

namespace PSX.Services;

/// <summary>
/// ACP provider for the managed Qoder CLI. PSX installs
/// <c>@qoder-ai/qodercli</c> under <c>runtime/qoder-current</c> after user
/// confirmation and launches it as <c>node &lt;entry&gt; --acp</c>.
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

    public IAgentConfigSource? ConfigSource { get; } = new QoderConfigSource();

    // Unauthenticated session/new can exceed 45s. Give headroom and map that
    // stall onto auth_required + managed login instead of a hard error.
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
    /// never as a normal chat path. Prefer the managed portable Node + entry so
    /// PATH shims are never required. Returns null when the install is incomplete
    /// so the shared session layer can surface a provider-neutral message.
    /// </summary>
    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var invocation = _runtime.TryBuildInteractivePowerShellInvocation();
        if (invocation == null)
            return null;

        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "qoder-cli",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {invocation}",
            StartingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Qoder documents interactive login as <c>qodercli login</c>, not
    /// <c>qodercli --acp --login</c>. Uses the same managed Node + entry.
    /// Returns null when the install is incomplete.
    /// </summary>
    public ShellProfile? CreateLoginTerminalProfile(string workingDirectory)
    {
        var invocation = _runtime.TryBuildInteractivePowerShellInvocation("login");
        if (invocation == null)
            return null;

        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "qoder-cli-login",
            Name = $"{Descriptor.DisplayName} 登录",
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {invocation}",
            StartingDirectory = workingDirectory
        };
    }
}
