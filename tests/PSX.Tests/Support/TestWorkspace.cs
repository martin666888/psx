using System.Diagnostics;
using PSX.Models;

namespace PSX.Tests.Support;

internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string path)
    {
        Path = path;
        Directory.CreateDirectory(Path);
    }

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public string Path { get; }

    public static TestWorkspace Create(string scope)
    {
        var safeScope = string.Concat(scope.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        var path = System.IO.Path.Combine(
            RepositoryRoot,
            "TestResults",
            "runtime",
            string.IsNullOrWhiteSpace(safeScope) ? "test" : safeScope,
            Guid.NewGuid().ToString("N"));
        return new TestWorkspace(path);
    }

    public AcpProcessSpec CreateTestAgentSpec(string? scenario = null)
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{System.IO.Path.DirectorySeparatorChar}Debug{System.IO.Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var executable = System.IO.Path.Combine(
            RepositoryRoot,
            "tests",
            "PSX.TestAgent",
            "bin",
            configuration,
            "net10.0",
            "PSX.TestAgent.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The Fake ACP Agent was not built.", executable);

        return new AcpProcessSpec
        {
            FileName = executable,
            WorkingDirectory = Path,
            Environment = new Dictionary<string, string?>
            {
                ["PSX_TEST_AGENT_SCENARIO"] = scenario ?? "default"
            }
        };
    }

    public void Dispose()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PSX_KEEP_TEST_ARTIFACTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        if (!Directory.Exists(Path))
            return;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
                Assert.Fail(failureMessage);
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "PSX.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Unable to locate the PSX repository root.");
    }
}
