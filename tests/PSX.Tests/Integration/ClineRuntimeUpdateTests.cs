using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Cline Qoder-style staged self-update: registry view + exact wrapper and
/// platform install into cline-next, four-gate validation, and startup promote.
/// First install remains seed npm ci into cline-current.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ClineRuntimeUpdateTests
{
    private const string SeedVersion = ClineAcpRuntime.SeededPackageVersion;

    [TestMethod]
    public async Task Refresh_NewerVersion_ViewsThenStagesExactWrapperAndPlatformAndFlipsPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerVersion_ViewsThenStagesExactWrapperAndPlatformAndFlipsPointer));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, SeedVersion);
        fixture.Configure(ClineViewScenario("3.1.0"), ClineInstallScenario("3.1.0", "cline-next"));
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.ClineActivePointerFile).Trim());
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(SeedVersion, snapshot.CurrentVersion);
        Assert.AreEqual("3.1.0", snapshot.PendingVersion);
        Assert.IsTrue(snapshot.HasPendingUpdate);

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(2, invocations);
        Assert.AreEqual("view", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, "cline@latest");
        Assert.AreEqual("install", invocations[1].Command);
        CollectionAssert.Contains(invocations[1].Arguments, "cline@3.1.0");
        CollectionAssert.Contains(invocations[1].Arguments, "@cline/cli-windows-x64@3.1.0");
        CollectionAssert.Contains(invocations[1].Arguments, "--ignore-scripts");
        CollectionAssert.Contains(invocations[1].Arguments, "--save-exact");
    }

    [TestMethod]
    public async Task Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, SeedVersion);
        fixture.Configure(ClineViewScenario(SeedVersion));
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.ClineActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations());
        Assert.AreEqual("view", fixture.ReadInvocations()[0].Command);
    }

    [TestMethod]
    public async Task Refresh_RegistryOlderThanCurrent_RefusesDowngrade()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_RegistryOlderThanCurrent_RefusesDowngrade));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, SeedVersion);
        fixture.Configure(ClineViewScenario("3.0.1"));
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "低于当前版本");
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task Refresh_MismatchedPlatform_LeavesCurrentAndDoesNotFlipPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_MismatchedPlatform_LeavesCurrentAndDoesNotFlipPointer));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, SeedVersion);
        fixture.Configure(
            ClineViewScenario("3.1.0"),
            new FakeNpmScenario
            {
                Command = "install",
                WorkingDirectoryName = "cline-next",
                CreateAdapter = false,
                CreateClaude = false,
                CreateCline = true,
                ClineVersion = "3.1.0",
                ClinePlatformVersion = "3.0.53"
            });
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.IsFalse(File.Exists(fixture.Paths.ClineActivePointerFile)
            && File.ReadAllText(fixture.Paths.ClineActivePointerFile).Trim() == "next");
        Assert.AreEqual(SeedVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public async Task PrepareForStartup_PointerNext_PromotesToClineCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_PointerNext_PromotesToClineCurrent));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, SeedVersion);
        fixture.Configure(ClineViewScenario("3.1.0"), ClineInstallScenario("3.1.0", "cline-next"));
        using var runtime = fixture.CreateClineRuntime();
        await runtime.RefreshAsync();

        await runtime.PrepareForStartupAsync();

        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.ClineActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineNextDirectory));
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual("3.1.0", snapshot.CurrentVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);
        StringAssert.StartsWith(spec.Arguments[0], fixture.Paths.ClineCurrentDirectory);
    }

    private static FakeNpmScenario ClineViewScenario(string version) => new()
    {
        Command = "view",
        WorkingDirectoryName = "runtime",
        StandardOutput = "\"" + version + "\"",
        CreateAdapter = false,
        CreateClaude = false
    };

    private static FakeNpmScenario ClineInstallScenario(string version, string workingDirectoryName) => new()
    {
        Command = "install",
        WorkingDirectoryName = workingDirectoryName,
        CreateAdapter = false,
        CreateClaude = false,
        CreateCline = true,
        ClineVersion = version
    };
}
