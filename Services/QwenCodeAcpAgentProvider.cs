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
    private readonly QwenCodeAcpRuntime _runtime;

    public QwenCodeAcpAgentProvider(QwenCodeAcpRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Runtime = runtime;
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

    public IAgentUsageSource? UsageSource { get; } = new QwenCodeSessionUsageSource();

    public IAgentConfigSource? ConfigSource { get; } = new QwenConfigSource();

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
    /// Only used for the explicit <c>/terminal</c> entry point and troubleshooting.
    /// Uses the resolved portable Node + package entry. Returns null when the
    /// install is incomplete so the shared session layer can surface a
    /// provider-neutral message — never fall back to a PATH <c>qwen</c> shim.
    /// </summary>
    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var command = _runtime.TryBuildInteractivePowerShellInvocation(sessionId);
        if (command == null)
            return null;

        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "qwen-code",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Qwen does not accept <c>--acp --login</c>. Interactive OAuth happens in
    /// the normal TUI (portable Node + package entry); credentials land in
    /// <c>~/.qwen</c> and the Agent workspace retries afterward.
    /// </summary>
    public ShellProfile? CreateLoginTerminalProfile(string workingDirectory)
    {
        var command = _runtime.TryBuildInteractivePowerShellInvocation(sessionId: null);
        if (command == null)
            return null;

        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "qwen-code-login",
            Name = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                PSX.Properties.Strings.LoginTabName,
                Descriptor.DisplayName),
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }
}
