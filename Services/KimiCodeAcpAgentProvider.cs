using PSX.Models;

namespace PSX.Services;

/// <summary>
/// ACP provider for the bundled Kimi Code CLI. Mirrors
/// <see cref="ClaudeAcpAgentProvider"/> but targets its own
/// <see cref="KimiCodeAcpRuntime"/> instance (Kimi and Claude have disjoint
/// dependency sets, so they must not share a runtime).
/// </summary>
public sealed class KimiCodeAcpAgentProvider : IAcpAgentProvider
{
    public KimiCodeAcpAgentProvider(KimiCodeAcpRuntime runtime)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-kimi",
        DisplayName: "Kimi Code",
        AssistantName: "Kimi",
        LegacyKeys: Array.Empty<string>())
    {
        IconKey = "kimi"
    };

    public IAcpAgentRuntime Runtime { get; }

    // Kimi Code 0.29.1's reverse ACP filesystem bridge (fs/read_text_file) can
    // hang on parallel/large reads and stall ACP input processing. Do not
    // advertise fs, so Kimi falls back to its own local filesystem tools and
    // never routes file contents through a large reverse JSON-RPC response.
    // Terminal stays enabled: login goes through standard ACP terminal-auth.
    public AcpClientCapabilityProfile ClientCapabilities { get; } = new()
    {
        FileSystemReadText = false,
        FileSystemWriteText = false,
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

    /// <summary>
    /// v1: show every command Kimi declares. We do not pre-seed a blacklist;
    /// a command is only hidden once it is proven non-executable in ACP mode
    /// with a deterministic test, at which point it joins a private filter set.
    /// </summary>
    public bool IsCommandVisible(string normalizedCommand) => true;

    /// <summary>
    /// Only used for the explicit <c>/terminal</c> entry point and troubleshooting,
    /// never as a normal chat path (login goes through standard ACP terminal-auth).
    /// </summary>
    public ShellProfile CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var escapedCwd = workingDirectory.Replace("'", "''");
        var command = string.IsNullOrWhiteSpace(sessionId)
            ? "kimi"
            : $"kimi --resume {sessionId}";

        return new ShellProfile
        {
            Id = "kimi-code",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }
}
