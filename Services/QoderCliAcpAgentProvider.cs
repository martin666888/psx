using PSX.Models;

namespace PSX.Services;

/// <summary>
/// ACP provider for the external Qoder CLI. PSX discovers and launches
/// <c>qodercli --acp</c> from PATH but does not install or update the CLI.
/// </summary>
public sealed class QoderCliAcpAgentProvider : IAcpAgentProvider
{
    public QoderCliAcpAgentProvider(QoderCliAcpRuntime runtime)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
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
    /// never as a normal chat path.
    /// </summary>
    public ShellProfile CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "qoder-cli",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; qodercli",
            StartingDirectory = workingDirectory
        };
    }
}
