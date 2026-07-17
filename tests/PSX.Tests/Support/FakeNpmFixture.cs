using System.Diagnostics;
using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Support;

internal sealed class FakeNpmFixture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly TestWorkspace _workspace;

    public FakeNpmFixture(string scope, TimeSpan? processTimeout = null)
    {
        _workspace = TestWorkspace.Create(scope);
        InstallDirectory = _workspace.Path;
        InvocationLogPath = Path.Combine(InstallDirectory, "fake-npm-invocations.jsonl");
        Configure();
        InstallFakeNode();
        InstallSeedFiles();
        Locator = new RuntimeLocator(InstallDirectory);
        Manager = new AcpRuntimeManager(
            Locator,
            Path.Combine(InstallDirectory, "logs"),
            processTimeout ?? TimeSpan.FromSeconds(10));
    }

    public string InstallDirectory { get; }
    public string InvocationLogPath { get; }
    public RuntimeLocator Locator { get; }
    public AcpRuntimeManager Manager { get; }
    public RuntimePaths Paths => Locator.Locate();

    public void Configure(params FakeNpmScenario[] scenarios)
    {
        var configuration = new
        {
            invocationLogPath = InvocationLogPath,
            scenarios,
            defaultScenario = new FakeNpmScenario
            {
                ExitCode = 99,
                StandardError = "Unexpected Fake npm invocation.",
                CreateAdapter = false,
                CreateClaude = false
            }
        };
        var path = NpmCliPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    public void CreateCompleteRuntime(string directory, string adapterVersion, string claudeVersion = "2.0.0-test")
    {
        WriteFile(
            Path.Combine(directory, "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"),
            "fake adapter");
        WriteJson(
            Path.Combine(directory, "node_modules", "@agentclientprotocol", "claude-agent-acp", "package.json"),
            new { version = adapterVersion });
        WriteJson(
            Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-agent-sdk", "package.json"),
            new { version = "0.0.0-test", claudeCodeVersion = claudeVersion });
        WriteFile(
            Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-agent-sdk-win32-x64", "claude.exe"),
            "fake claude");
    }

    public void WritePointer(string value)
    {
        Directory.CreateDirectory(Paths.RuntimeRoot);
        File.WriteAllText(Paths.AcpActivePointerFile, value);
    }

    public IReadOnlyList<FakeNpmInvocation> ReadInvocations()
    {
        if (!File.Exists(InvocationLogPath))
            return [];
        return File.ReadAllLines(InvocationLogPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<FakeNpmInvocation>(line, JsonOptions)!)
            .ToArray();
    }

    public static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Manager.Dispose();
        _workspace.Dispose();
    }

    private void InstallFakeNode()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var outputDirectory = Path.Combine(
            TestWorkspace.RepositoryRoot,
            "tests",
            "PSX.TestNpm",
            "bin",
            configuration,
            "net10.0");
        var sourceExecutable = Path.Combine(outputDirectory, "PSX.TestNpm.exe");
        if (!File.Exists(sourceExecutable))
            throw new FileNotFoundException("The Fake npm process was not built.", sourceExecutable);

        var nodeDirectory = Path.Combine(InstallDirectory, "tools", "node");
        Directory.CreateDirectory(nodeDirectory);
        foreach (var source in Directory.EnumerateFiles(outputDirectory, "PSX.TestNpm.*"))
            File.Copy(source, Path.Combine(nodeDirectory, Path.GetFileName(source)), overwrite: true);
        File.Copy(sourceExecutable, Path.Combine(nodeDirectory, "node.exe"), overwrite: true);
    }

    private void InstallSeedFiles()
    {
        var destination = Path.Combine(InstallDirectory, "tools", "acp-seed");
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
        {
            File.Copy(
                Path.Combine(TestWorkspace.RepositoryRoot, "tools", "acp-seed", name),
                Path.Combine(destination, name),
                overwrite: true);
        }
    }

    private string NpmCliPath() => Path.Combine(
        InstallDirectory,
        "tools",
        "node",
        "node_modules",
        "npm",
        "bin",
        "npm-cli.js");

    private static void WriteJson(string path, object value) =>
        WriteFile(path, JsonSerializer.Serialize(value));

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
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
    public string? AdapterVersion { get; set; }
    public string? ClaudeCodeVersion { get; set; }
}

internal sealed class FakeNpmInvocation
{
    public int ProcessId { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public string Command { get; set; } = "";
}
