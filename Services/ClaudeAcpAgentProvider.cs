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

    // Claude Code writes per-session JSONL under ~/.claude/projects; the Usage
    // panel reads exact token usage (including cache hits) from there, scoped to
    // PSX-owned session ids only.
    public IAgentUsageSource? UsageSource { get; } = new ClaudeSessionUsageSource();

    // User-level settings / MCP / skills under CLAUDE_CONFIG_DIR ?? ~/.claude
    // (plus ~/.claude.json mcpServers). Distinct from live ACP config options.
    public IAgentConfigSource? ConfigSource { get; } = new ClaudeConfigSource();

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-claude",
        DisplayName: "Claude Code",
        AssistantName: "Claude",
        LegacyKeys: new[] { "claude-cli" })
    {
        IconKey = "claude"
    };

    public IAcpAgentRuntime Runtime { get; }

    // Claude uses the full ACP client surface, including the reverse filesystem
    // bridge (fs/read_text_file, fs/write_text_file).
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
