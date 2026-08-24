using PSX.Models;

namespace PSX.Services;

/// <summary>
/// ACP provider for the bundled OpenCode CLI. Mirrors
/// <see cref="QwenCodeAcpAgentProvider"/> but targets its own
/// <see cref="OpencodeAcpRuntime"/> instance (a native Bun-compiled
/// executable, so no portable Node is involved at session time).
/// </summary>
public sealed class OpencodeAcpAgentProvider : IAcpAgentProvider
{
    private readonly OpencodeAcpRuntime _runtime;

    public OpencodeAcpAgentProvider(OpencodeAcpRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Runtime = runtime;
    }

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-opencode",
        DisplayName: "OpenCode",
        AssistantName: "OpenCode",
        LegacyKeys: Array.Empty<string>())
    {
        IconKey = "opencode"
    };

    public IAcpAgentRuntime Runtime { get; }

    public IAgentUsageSource? UsageSource { get; } = new OpencodeSessionUsageSource();

    public IAgentConfigSource? ConfigSource { get; } = new OpencodeConfigSource();

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
    /// The first session/new budget is wider than the 30s default because the
    /// ~175MB Bun single-file executable can cold-start slower than a Node
    /// entry point.
    /// </summary>
    public TimeSpan NewSessionTimeout => TimeSpan.FromSeconds(60);

    /// <summary>
    /// Only used for the explicit <c>/terminal</c> entry point and
    /// troubleshooting. Uses the resolved managed executable (never a PATH
    /// <c>opencode</c> shim). Returns null when the install is incomplete so
    /// the shared session layer can surface a provider-neutral message.
    /// </summary>
    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var extraArgument = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : $"--session {QuoteForPowerShell(sessionId)}";
        var command = _runtime.TryBuildInteractivePowerShellInvocation(extraArgument);
        if (command == null)
            return null;

        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "opencode",
            Name = Descriptor.DisplayName,
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// OpenCode does not accept <c>--acp --login</c>. Interactive provider
    /// login happens via <c>opencode auth login</c> in the normal TUI;
    /// credentials land in <c>~/.local/share/opencode/auth.json</c> and the
    /// Agent workspace retries afterward.
    /// </summary>
    public ShellProfile? CreateLoginTerminalProfile(string workingDirectory)
    {
        var command = _runtime.TryBuildInteractivePowerShellInvocation("auth login");
        if (command == null)
            return null;

        var escapedCwd = workingDirectory.Replace("'", "''");
        return new ShellProfile
        {
            Id = "opencode-login",
            Name = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                PSX.Properties.Strings.LoginTabName,
                Descriptor.DisplayName),
            Command = "powershell.exe",
            Arguments = $"-NoExit -Command Set-Location -LiteralPath '{escapedCwd}'; {command}",
            StartingDirectory = workingDirectory
        };
    }

    private static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''") + "'";
}
