using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class NpmRuntimeProcessRunnerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunAsync_LifecycleShell_UsesPortableNode(bool conflictingNodeOnPath)
    {
        using var workspace = TestWorkspace.Create(nameof(RunAsync_LifecycleShell_UsesPortableNode));
        var sourceNode = Path.Combine(TestWorkspace.RepositoryRoot,
            "TestResults", "node22", "node-v22.23.1-win-x64", "node.exe");
        if (!File.Exists(sourceNode))
            Assert.Inconclusive("Node 22 toolchain is not available under TestResults/node22.");

        var portableDirectory = Path.Combine(workspace.Path, "portable node");
        Directory.CreateDirectory(portableDirectory);
        var portableNode = Path.Combine(portableDirectory, "node.exe");
        File.Copy(sourceNode, portableNode);
        var otherDirectory = Path.Combine(workspace.Path, "other-node");
        Directory.CreateDirectory(otherDirectory);
        File.WriteAllText(Path.Combine(otherDirectory, "node.cmd"), "@exit /b 93\r\n");
        var script = Path.Combine(workspace.Path, "lifecycle.cjs");
        File.WriteAllText(script, """
            const { execSync } = require('node:child_process');
            process.stdout.write(execSync('node -p process.execPath', { encoding: 'utf8' }));
            """);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var testPath = conflictingNodeOnPath ? otherDirectory : null;
        try
        {
            Environment.SetEnvironmentVariable("PATH", testPath);
            var runner = new NpmRuntimeProcessRunner(TimeSpan.FromSeconds(30), _ => { });
            var result = await runner.RunAsync(portableNode, script, workspace.Path,
                "lifecycle Node probe", Array.Empty<string>(), CancellationToken.None);

            Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind, result.Message);
            Assert.AreEqual(portableNode, result.Stdout.Trim(), ignoreCase: true);
            Assert.AreEqual(testPath, Environment.GetEnvironmentVariable("PATH"),
                "Only the child process PATH should change.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
