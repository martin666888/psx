using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QoderCliAcpRuntimeTests
{
    [TestMethod]
    public async Task PrepareForStartupAsync_DiscoversPathWithoutExecuting()
    {
        using var workspace = TestWorkspace.Create(nameof(PrepareForStartupAsync_DiscoversPathWithoutExecuting));
        var binDirectory = CreateStubQoderCli(workspace, "0.2.11");
        using var pathScope = new TemporaryPathPrepend(binDirectory);

        var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));
        await runtime.PrepareForStartupAsync();

        Assert.IsTrue(runtime.HasDiscoveredEntry);
        Assert.IsFalse(runtime.IsReady());
        Assert.IsFalse(runtime.VersionProbeAttempted);
    }

    [TestMethod]
    public async Task EnsureInstalledAsync_AcceptsMinimumCompatibleVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(EnsureInstalledAsync_AcceptsMinimumCompatibleVersion));
        var binDirectory = CreateStubQoderCli(workspace, "0.2.11");
        using var pathScope = new TemporaryPathPrepend(binDirectory);

        var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));
        await runtime.PrepareForStartupAsync();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual("0.2.11", runtime.GetVersionSnapshot().CurrentVersion);
    }

    [TestMethod]
    public async Task EnsureInstalledAsync_RejectsUnsupportedVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(EnsureInstalledAsync_RejectsUnsupportedVersion));
        var binDirectory = CreateStubQoderCli(workspace, "0.2.10");
        using var pathScope = new TemporaryPathPrepend(binDirectory);

        var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));
        await runtime.PrepareForStartupAsync();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.IsFalse(runtime.IsReady());
        StringAssert.Contains(result.Message, "0.2.10");
        StringAssert.Contains(result.Message, "0.2.11");
    }

    [TestMethod]
    public async Task EnsureInstalledAsync_WhenMissingOnPath_ReturnsInstallGuidance()
    {
        using var workspace = TestWorkspace.Create(nameof(EnsureInstalledAsync_WhenMissingOnPath_ReturnsInstallGuidance));
        var emptyBin = Path.Combine(workspace.Path, "empty-bin");
        Directory.CreateDirectory(emptyBin);
        QoderCliAcpRuntime.SkipNpmShimDiscovery = true;
        using var pathScope = new IsolatedPath(emptyBin);
        try
        {
            var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));
            await runtime.PrepareForStartupAsync();

            var result = await runtime.EnsureInstalledAsync();

            Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
            Assert.IsFalse(runtime.HasDiscoveredEntry);
            StringAssert.Contains(result.Message, "qodercli");
        }
        finally
        {
            QoderCliAcpRuntime.SkipNpmShimDiscovery = false;
        }
    }

    [TestMethod]
    public async Task RefreshAsync_ReportsExternalOwnershipMessage()
    {
        using var workspace = TestWorkspace.Create(nameof(RefreshAsync_ReportsExternalOwnershipMessage));
        var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        StringAssert.Contains(result.Message, "Qoder 由外部管理");
    }

    [TestMethod]
    public async Task CreateProcessSpec_UsesDiscoveredCmdWithAcpFlag()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_UsesDiscoveredCmdWithAcpFlag));
        var binDirectory = CreateStubQoderCli(workspace, "0.2.11");
        using var pathScope = new TemporaryPathPrepend(binDirectory);

        var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));
        await runtime.EnsureInstalledAsync();

        var spec = runtime.CreateProcessSpec(workspace.Path);

        Assert.AreEqual(Path.Combine(binDirectory, "qodercli.cmd"), spec.FileName);
        Assert.HasCount(1, spec.Arguments);
        Assert.AreEqual("--acp", spec.Arguments[0]);
    }

    [TestMethod]
    public void DiscoverEntryPath_PrefersCmdOverBareExecutable()
    {
        using var workspace = TestWorkspace.Create(nameof(DiscoverEntryPath_PrefersCmdOverBareExecutable));
        var binDirectory = Path.Combine(workspace.Path, "bin");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(Path.Combine(binDirectory, "qodercli"), "@echo bare");
        File.WriteAllText(Path.Combine(binDirectory, "qodercli.cmd"), "@echo cmd");
        using var pathScope = new TemporaryPathPrepend(binDirectory);

        var discovered = QoderCliAcpRuntime.DiscoverEntryPath();

        Assert.AreEqual(Path.Combine(binDirectory, "qodercli.cmd"), discovered);
    }

    private static string CreateStubQoderCli(TestWorkspace workspace, string version)
    {
        var binDirectory = Path.Combine(workspace.Path, "bin");
        Directory.CreateDirectory(binDirectory);
        var cmdPath = Path.Combine(binDirectory, "qodercli.cmd");
        File.WriteAllText(cmdPath,
            """
            @echo off
            if "%1"=="--version" (
              echo Qoder CLI %VERSION%
              exit /b 0
            )
            exit /b 1
            """.Replace("%VERSION%", version, StringComparison.Ordinal));
        return binDirectory;
    }

    private sealed class TemporaryPathPrepend : IDisposable
    {
        private readonly string? _previousPath;

        public TemporaryPathPrepend(string directory)
        {
            _previousPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable(
                "PATH",
                directory + Path.PathSeparator + (_previousPath ?? string.Empty));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _previousPath);
        }
    }

    private sealed class IsolatedPath : IDisposable
    {
        private readonly string? _previousPath;

        public IsolatedPath(string directory)
        {
            _previousPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", directory);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _previousPath);
        }
    }
}
