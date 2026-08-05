using PSX.Models;

namespace PSX.Services;

/// <summary>
/// ACP provider for the bundled Qwen Code CLI. Mirrors
/// <see cref="KimiCodeAcpAgentProvider"/> but targets its own
/// <see cref="QwenCodeAcpRuntime"/> instance (Qwen and Claude have disjoint
/// dependency sets, so they must not share a runtime).
/// </summary>
public sealed class QwenCodeAcpAgentProvider : IAcpAgentProvider
{
    public QwenCodeAcpAgentProvider(QwenCodeAcpRuntime runtime)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-qwen",
        DisplayName: "Qwen Code",
        AssistantName: "Qwen",
        LegacyKeys: Array.Empty<string>())
    {
        IconKey = "qwen"
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
        var command = string.IsNullOrWhiteSpace(sessionId)
            ? "qwen"
            : $"qwen --resume {sessionId}";

        return new ShellProfile
        {
            Id = "qwen-code",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }
}
