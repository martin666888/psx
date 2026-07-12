using PSX.Models;

namespace PSX.Services;

public sealed class ClaudeAcpAgentProvider : IAcpAgentProvider
{
    private static readonly HashSet<string> HiddenCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/model"
    };

    public ClaudeAcpAgentProvider(AcpRuntimeManager runtime)
    {
        Runtime = runtime;
    }

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-claude",
        DisplayName: "Claude Code",
        AssistantName: "Claude",
        LegacyKeys: new[] { "claude-cli" });

    public IAcpAgentRuntime Runtime { get; }

    public object CreateNewSessionParameters(string workingDirectory)
    {
        return new
        {
            cwd = workingDirectory,
            mcpServers = Array.Empty<object>()
        };
    }

    public object CreateLoadSessionParameters(string sessionId, string workingDirectory)
    {
        return new
        {
            sessionId,
            cwd = workingDirectory,
            mcpServers = Array.Empty<object>()
        };
    }

    public bool IsCommandVisible(string normalizedCommand)
    {
        return !HiddenCommands.Contains(normalizedCommand);
    }

    public ShellProfile CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var escapedCwd = workingDirectory.Replace("'", "''");
        var command = string.IsNullOrWhiteSpace(sessionId)
            ? "claude"
            : $"claude --resume {sessionId}";

        return new ShellProfile
        {
            Id = "claude-code",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }
}
