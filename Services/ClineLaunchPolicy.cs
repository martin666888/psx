using System.Text;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Single source of truth for every managed Cline entry point. The ACP process,
/// login terminal, TUI and smoke checks must not grow independent environment
/// policy.
/// </summary>
internal sealed class ClineLaunchPolicy
{
    private static readonly string[] ClearedVariables =
    [
        "CLINE_PROVIDER",
        "CLINE_MODEL",
        "CLINE_HUB_ADDRESS",
        "CLINE_DATA_DIR",
        "CLINE_DIR",
        "CLINE_API_KEY"
    ];

    public IReadOnlyDictionary<string, string?> CreateEnvironment(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLINE_NO_AUTO_UPDATE"] = "1",
            ["CLINE_SESSION_BACKEND_MODE"] = "local",
            ["CLINE_BIN_PATH"] = executablePath
        };
        foreach (var name in ClearedVariables)
            environment[name] = null;
        return environment;
    }

    public AcpProcessSpec CreateAcpProcessSpec(
        string nodePath,
        string wrapperPath,
        string executablePath,
        string workingDirectory)
    {
        return new AcpProcessSpec
        {
            FileName = nodePath,
            WorkingDirectory = workingDirectory,
            Arguments = [wrapperPath, "--acp", "--auto-approve", "false"],
            Environment = CreateEnvironment(executablePath)
        };
    }

    public string CreatePowerShellInvocation(
        string nodePath,
        string wrapperPath,
        string executablePath,
        params string[] arguments)
    {
        var command = new StringBuilder();
        foreach (var name in ClearedVariables)
            command.Append("Remove-Item Env:").Append(name).Append(" -ErrorAction SilentlyContinue; ");

        AppendEnvironmentAssignment(command, "CLINE_NO_AUTO_UPDATE", "1");
        AppendEnvironmentAssignment(command, "CLINE_SESSION_BACKEND_MODE", "local");
        AppendEnvironmentAssignment(command, "CLINE_BIN_PATH", executablePath);
        command.Append("& ").Append(QuoteForPowerShell(nodePath))
            .Append(' ').Append(QuoteForPowerShell(wrapperPath));
        foreach (var argument in arguments)
            command.Append(' ').Append(QuoteForPowerShell(argument));
        return command.ToString();
    }

    private static void AppendEnvironmentAssignment(StringBuilder command, string name, string value)
        => command.Append("$env:").Append(name).Append(" = ")
            .Append(QuoteForPowerShell(value)).Append("; ");

    internal static string QuoteForPowerShell(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
