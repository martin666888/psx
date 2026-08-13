using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ClineRuntimeInstallTests
{
    [TestMethod]
    public async Task EnsureInstalled_UsesCiIgnoreScriptsAndActivatesPinnedRuntime()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_UsesCiIgnoreScriptsAndActivatesPinnedRuntime));
        fixture.InstallClineSeed();
        fixture.Configure(InstallScenario());
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.ClineActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineInstallingDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineNextDirectory));
        var invocation = fixture.ReadInvocations().Single();
        Assert.AreEqual("ci", invocation.Command);
        CollectionAssert.Contains(invocation.Arguments, "--ignore-scripts");
        CollectionAssert.Contains(invocation.Arguments, "--include=optional");
        StringAssert.EndsWith(invocation.WorkingDirectory, "cline-installing");
    }

    [TestMethod]
    public async Task EnsureInstalled_FailedInstall_PreservesPreviousCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_FailedInstall_PreservesPreviousCurrent));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.52");
        fixture.Configure(new FakeNpmScenario
        {
            Command = "ci",
            WorkingDirectoryName = "cline-installing",
            ExitCode = 7,
            StandardError = "install failed",
            CreateAdapter = false,
            CreateClaude = false
        });
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.AreEqual("3.0.52", runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(Directory.Exists(fixture.Paths.ClineCurrentDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineInstallingDirectory));
    }

    [TestMethod]
    public async Task EnsureInstalled_MismatchedPlatformVersion_PreservesPreviousCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_MismatchedPlatformVersion_PreservesPreviousCurrent));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.52");
        fixture.Configure(new FakeNpmScenario
        {
            Command = "ci",
            WorkingDirectoryName = "cline-installing",
            CreateAdapter = false,
            CreateClaude = false,
            CreateCline = true,
            ClineVersion = ClineAcpRuntime.SeededPackageVersion,
            ClinePlatformVersion = "3.0.52"
        });
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.AreEqual("3.0.52", runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(Directory.Exists(fixture.Paths.ClineCurrentDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineInstallingDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));
    }

    [TestMethod]
    public async Task EnsureInstalled_FailedSmoke_PreservesPreviousCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_FailedSmoke_PreservesPreviousCurrent));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.52");
        fixture.Configure(new FakeNpmScenario
        {
            Command = "ci",
            WorkingDirectoryName = "cline-installing",
            CreateAdapter = false,
            CreateClaude = false,
            CreateCline = true,
            ClineVersion = ClineAcpRuntime.SeededPackageVersion,
            ClineSmokeFails = true
        });
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.AreEqual("3.0.52", runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(Directory.Exists(fixture.Paths.ClineCurrentDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineInstallingDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));
    }

    [TestMethod]
    public async Task EnsureInstalled_CancelledNpm_ReleasesProcessAndPreservesPreviousCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_CancelledNpm_ReleasesProcessAndPreservesPreviousCurrent));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.52");
        fixture.Configure(new FakeNpmScenario
        {
            Command = "ci",
            WorkingDirectoryName = "cline-installing",
            Hang = true,
            CreateAdapter = false,
            CreateClaude = false
        });
        using var runtime = fixture.CreateClineRuntime();
        using var cancellation = new CancellationTokenSource();
        var operation = runtime.EnsureInstalledAsync(cancellation.Token);
        await TestWorkspace.WaitUntilAsync(
            () => fixture.ReadInvocations().Count == 1,
            TimeSpan.FromSeconds(5),
            "Fake npm did not start.");
        var processId = fixture.ReadInvocations().Single().ProcessId;

        cancellation.Cancel();
        var result = await operation;

        Assert.AreEqual(AcpRuntimeOperationKind.Cancelled, result.Kind);
        Assert.IsFalse(FakeNpmFixture.IsProcessRunning(processId));
        Assert.AreEqual("3.0.52", runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(Directory.Exists(fixture.Paths.ClineCurrentDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineInstallingDirectory));
        var moved = fixture.Paths.ClineCurrentDirectory + ".released";
        Directory.Move(fixture.Paths.ClineCurrentDirectory, moved);
        Directory.Move(moved, fixture.Paths.ClineCurrentDirectory);
    }

    [TestMethod]
    public async Task PrepareForStartup_MissingCurrent_RestoresRollbackWithoutNetwork()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_MissingCurrent_RestoresRollbackWithoutNetwork));
        fixture.CreateClineInstall(fixture.Paths.ClineRollbackDirectory, ClineAcpRuntime.SeededPackageVersion);
        using var runtime = fixture.CreateClineRuntime();

        await runtime.PrepareForStartupAsync();

        Assert.IsTrue(runtime.IsReady());
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));
        Assert.HasCount(0, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task PrepareForStartup_IncompleteCurrent_RestoresRollbackWithoutNetwork()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_IncompleteCurrent_RestoresRollbackWithoutNetwork));
        fixture.CreateClineInstall(fixture.Paths.ClineRollbackDirectory, ClineAcpRuntime.SeededPackageVersion);
        Directory.CreateDirectory(fixture.Paths.ClineCurrentDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.ClineCurrentDirectory, "broken.txt"), "incomplete");
        using var runtime = fixture.CreateClineRuntime();

        await runtime.PrepareForStartupAsync();

        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(ClineAcpRuntime.SeededPackageVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));
        Assert.HasCount(0, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task EnsureInstalled_SuccessfulUpgrade_DoesNotResurrectLeftoverRollback()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_SuccessfulUpgrade_DoesNotResurrectLeftoverRollback));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.52");
        fixture.Configure(InstallScenario());
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual(ClineAcpRuntime.SeededPackageVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));

        fixture.CreateClineInstall(fixture.Paths.ClineRollbackDirectory, "3.0.52");
        await runtime.PrepareForStartupAsync();

        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(ClineAcpRuntime.SeededPackageVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineRollbackDirectory));
        Assert.HasCount(1, fixture.ReadInvocations());
    }

    private static FakeNpmScenario InstallScenario() => new()
    {
        Command = "ci",
        WorkingDirectoryName = "cline-installing",
        CreateAdapter = false,
        CreateClaude = false,
        CreateCline = true,
        ClineVersion = ClineAcpRuntime.SeededPackageVersion
    };
}
