using System.Text.Json;

if (args.Length < 2)
    return 64;

// `node <staged>/dist/main.mjs --version` — the Kimi staged smoke check.
// Handled before configuration parsing because args[0] is the package entry
// file here, not the fake npm configuration path.
if (args[0].EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) && args.Contains("--version"))
{
    var distDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]));
    var packageDirectory = distDirectory == null ? null : Path.GetDirectoryName(distDirectory);
    if (packageDirectory == null)
        return 1;
    // Test seam: a marker file forces the smoke check to fail.
    if (File.Exists(Path.Combine(packageDirectory, "smoke-fail.marker")))
        return 1;
    try
    {
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(packageDirectory, "package.json")).ConfigureAwait(false));
        var packageVersion = manifest.RootElement.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(packageVersion))
            return 1;
        Console.Out.WriteLine(packageVersion);
        return 0;
    }
    catch
    {
        return 1;
    }
}

var configPath = args[0];
FakeNpmConfiguration? configuration;
try
{
    configuration = JsonSerializer.Deserialize<FakeNpmConfiguration>(
        await File.ReadAllTextAsync(configPath).ConfigureAwait(false),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unable to load Fake npm configuration: {ex.Message}");
    return 65;
}

if (configuration == null)
    return 65;

var command = args[1];
var workingDirectory = Environment.CurrentDirectory;
var scenario = configuration.Scenarios.FirstOrDefault(candidate =>
    (string.IsNullOrEmpty(candidate.Command) || string.Equals(candidate.Command, command, StringComparison.OrdinalIgnoreCase))
    && (string.IsNullOrEmpty(candidate.WorkingDirectoryName)
        || string.Equals(candidate.WorkingDirectoryName, Path.GetFileName(workingDirectory), StringComparison.OrdinalIgnoreCase)))
    ?? configuration.DefaultScenario
    ?? new FakeNpmScenario();

if (!string.IsNullOrWhiteSpace(configuration.InvocationLogPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(configuration.InvocationLogPath)!);
    var invocation = JsonSerializer.Serialize(new
    {
        processId = Environment.ProcessId,
        workingDirectory,
        arguments = args,
        command
    });
    await File.AppendAllTextAsync(configuration.InvocationLogPath, invocation + Environment.NewLine).ConfigureAwait(false);
}

if (!string.IsNullOrWhiteSpace(scenario.StandardOutput))
    Console.Out.WriteLine(scenario.StandardOutput);
if (!string.IsNullOrWhiteSpace(scenario.StandardError))
    Console.Error.WriteLine(scenario.StandardError);

if (scenario.DelayMilliseconds > 0)
    await Task.Delay(scenario.DelayMilliseconds).ConfigureAwait(false);
if (scenario.Hang)
    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);

if (scenario.CreateAdapter)
{
    WriteFile(
        Path.Combine(workingDirectory, "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"),
        "console.log('fake ACP adapter');");
    WriteJson(
        Path.Combine(workingDirectory, "node_modules", "@agentclientprotocol", "claude-agent-acp", "package.json"),
        new { name = "@agentclientprotocol/claude-agent-acp", version = scenario.AdapterVersion ?? "1.0.0" });
    WriteJson(
        Path.Combine(workingDirectory, "node_modules", "@anthropic-ai", "claude-agent-sdk", "package.json"),
        new { name = "@anthropic-ai/claude-agent-sdk", version = "0.0.0-test", claudeCodeVersion = scenario.ClaudeCodeVersion ?? "2.0.0-test" });
}

if (scenario.CreateClaude)
{
    WriteFile(
        Path.Combine(workingDirectory, "node_modules", "@anthropic-ai", "claude-agent-sdk-win32-x64", "claude.exe"),
        "fake claude executable");
}

if (scenario.CreateKimi)
{
    WriteJson(
        Path.Combine(workingDirectory, "node_modules", "@moonshot-ai", "kimi-code", "package.json"),
        new
        {
            name = "@moonshot-ai/kimi-code",
            version = scenario.KimiVersion ?? "0.30.0",
            bin = new Dictionary<string, string> { ["kimi"] = "dist/main.mjs" }
        });
    WriteFile(
        Path.Combine(workingDirectory, "node_modules", "@moonshot-ai", "kimi-code", "dist", "main.mjs"),
        "// fake kimi acp entry");
    if (scenario.KimiSmokeFails)
    {
        // Makes the later `node main.mjs --version` smoke check exit 1.
        WriteFile(
            Path.Combine(workingDirectory, "node_modules", "@moonshot-ai", "kimi-code", "smoke-fail.marker"),
            "fail");
    }
}

return scenario.ExitCode;

static void WriteFile(string path, string contents)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
}

static void WriteJson(string path, object value) =>
    WriteFile(path, JsonSerializer.Serialize(value));

internal sealed class FakeNpmConfiguration
{
    public string InvocationLogPath { get; set; } = "";
    public List<FakeNpmScenario> Scenarios { get; set; } = [];
    public FakeNpmScenario? DefaultScenario { get; set; }
}

internal sealed class FakeNpmScenario
{
    public string Command { get; set; } = "";
    public string WorkingDirectoryName { get; set; } = "";
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public int DelayMilliseconds { get; set; }
    public bool Hang { get; set; }
    public bool CreateAdapter { get; set; } = true;
    public bool CreateClaude { get; set; } = true;
    public bool CreateKimi { get; set; }
    public bool KimiSmokeFails { get; set; }
    public string? AdapterVersion { get; set; }
    public string? ClaudeCodeVersion { get; set; }
    public string? KimiVersion { get; set; }
}
