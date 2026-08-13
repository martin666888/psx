using PSX.Models;

namespace PSX.Services;

/// <summary>Managed Cline CLI provider using its official stdio ACP entry.</summary>
public sealed class ClineAcpAgentProvider : IAcpAgentProvider
{
    private readonly ClineAcpRuntime _runtime;

    public ClineAcpAgentProvider(ClineAcpRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Runtime = runtime;
    }

    public AgentDescriptor Descriptor { get; } = new(
        Key: "acp-cline",
        DisplayName: "Cline",
        AssistantName: "Cline",
        LegacyKeys: Array.Empty<string>())
    {
        IconKey = "cline"
    };

    public IAcpAgentRuntime Runtime { get; }

    public IAcpProviderCompatibility Compatibility { get; } = new ClineAcpProviderCompatibility();

    public IAgentUsageSource? UsageSource => null;

    public IAgentConfigSource? ConfigSource { get; } = new ClineConfigSource();

    public AcpClientCapabilityProfile ClientCapabilities { get; } = new()
    {
        FileSystemReadText = true,
        FileSystemWriteText = true,
        Terminal = true,
        SessionBooleanConfig = true,
        ElicitationFormUrl = true,
        TerminalOutputMeta = true
    };

    public object CreateNewSessionParameters(string workingDirectory) => new
    {
        cwd = workingDirectory,
        mcpServers = Array.Empty<object>()
    };

    public object CreateRestoreSessionParameters(string sessionId, string workingDirectory) => new
    {
        sessionId,
        cwd = workingDirectory,
        mcpServers = Array.Empty<object>()
    };

    public bool IsCommandVisible(string normalizedCommand) => true;

    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId)
    {
        var arguments = new List<string> { "--tui" };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            arguments.Add("--id");
            arguments.Add(sessionId);
        }

        return CreateTerminalProfile(
            "cline-cli",
            Descriptor.DisplayName,
            workingDirectory,
            _runtime.TryBuildInteractivePowerShellInvocation(arguments.ToArray()));
    }

    public ShellProfile? CreateLoginTerminalProfile(string workingDirectory)
        => CreateTerminalProfile(
            "cline-cli-login",
            $"{Descriptor.DisplayName} 登录",
            workingDirectory,
            _runtime.TryBuildInteractivePowerShellInvocation("auth"));

    private static ShellProfile? CreateTerminalProfile(
        string id,
        string name,
        string workingDirectory,
        string? invocation)
    {
        if (invocation == null)
            return null;

        return new ShellProfile
        {
            Id = id,
            Name = name,
            Command = "powershell.exe",
            Arguments =
                $"-NoExit -Command Set-Location -LiteralPath {ClineLaunchPolicy.QuoteForPowerShell(workingDirectory)}; {invocation}",
            StartingDirectory = workingDirectory
        };
    }
}
