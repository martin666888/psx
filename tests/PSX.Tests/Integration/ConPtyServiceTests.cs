using System.Diagnostics;
using System.Text.Json;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Desktop")]
[DoNotParallelize]
public sealed class ConPtyServiceTests
{
    [TestMethod]
    public async Task DesktopProbe_ExercisesOutputExitResizeAndProcessCleanup()
    {
        using var workspace = TestWorkspace.Create(nameof(DesktopProbe_ExercisesOutputExitResizeAndProcessCleanup));
        var resultPath = Path.Combine(workspace.Path, "desktop-probe-result.json");
        var executable = ResolveDesktopProbeExecutable();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workspace.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { resultPath }
        }) ?? throw new AssertFailedException("The desktop probe process did not start.");

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.IsTrue(File.Exists(resultPath), "The desktop probe did not write its result inside the test workspace.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
        var result = document.RootElement;
        var error = result.TryGetProperty("Error", out var errorElement)
            ? errorElement.GetString()
            : null;

        Assert.AreEqual(0, process.ExitCode, error ?? result.GetRawText());
        Assert.IsTrue(result.GetProperty("OutputReceived").GetBoolean());
        Assert.IsTrue(result.GetProperty("ExitEventReceived").GetBoolean());
        Assert.IsTrue(result.GetProperty("NaturalExitRemovedSession").GetBoolean());
        Assert.IsTrue(result.GetProperty("ProcessWasRunningBeforeClose").GetBoolean());
        Assert.IsTrue(result.GetProperty("CloseRemovedSession").GetBoolean());
        Assert.IsTrue(result.GetProperty("CloseTerminatedProcess").GetBoolean());
    }

    private static string ResolveDesktopProbeExecutable()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var executable = Path.Combine(
            TestWorkspace.RepositoryRoot,
            "tests",
            "PSX.DesktopProbe",
            "bin",
            configuration,
            "net10.0-windows",
            "PSX.DesktopProbe.exe");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException("The desktop probe was not built.", executable);
    }
}
