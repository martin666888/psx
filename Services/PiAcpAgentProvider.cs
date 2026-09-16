using PSX.Models;

namespace PSX.Services;

public sealed class PiAcpAgentProvider(PiAcpRuntime runtime) : IAcpAgentProvider
{
    public AgentDescriptor Descriptor { get; } = new("acp-pi", "Pi", "Pi", []) { IconKey = "pi" };
    public IAcpAgentRuntime Runtime { get; } = runtime;
    public IAgentUsageSource UsageSource { get; } = new PiSessionUsageSource();
    public IAgentConfigSource ConfigSource { get; } = new PiConfigSource();
    public AcpClientCapabilityProfile ClientCapabilities { get; } = new()
    {
        SessionBooleanConfig = true,
        ElicitationFormUrl = true
    };
    public object CreateNewSessionParameters(string workingDirectory) => new { cwd = workingDirectory, mcpServers = Array.Empty<object>() };
    public object CreateRestoreSessionParameters(string sessionId, string workingDirectory) => new { sessionId, cwd = workingDirectory, mcpServers = Array.Empty<object>() };
    public bool IsCommandVisible(string normalizedCommand) => true;
    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId) => CreateTerminal(workingDirectory, sessionId, false);
    public ShellProfile? CreateLoginTerminalProfile(string workingDirectory) => CreateTerminal(workingDirectory, null, true);

    private ShellProfile? CreateTerminal(string cwd, string? sessionId, bool login)
    {
        var command = runtime.InteractiveCommand(sessionId);
        return command == null ? null : new ShellProfile
        {
            Id = login ? "pi-login" : "pi",
            Name = login ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, Properties.Strings.LoginTabName, "Pi") : "Pi",
            Command = "powershell.exe",
            Arguments = "-NoExit -Command Set-Location -LiteralPath '" + cwd.Replace("'", "''") + "'; " + command,
            StartingDirectory = cwd
        };
    }
}
