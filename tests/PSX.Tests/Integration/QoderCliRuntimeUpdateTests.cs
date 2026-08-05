using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Qoder CLI Claude-style managed install + staged self-update: first install
/// into runtime/qoder-current with --ignore-scripts, registry view + exact
/// staging into qoder-next, four-gate validation, and startup promote.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class QoderCliRuntimeUpdateTests
{
    private const string SeedVersion = QoderCliAcpRuntime.SeededPackageVersion;

    [TestMethod]
    public async Task EnsureInstalled_Missing_InstallsPinnedVersionWithIgnoreScripts()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_Missing_InstallsPinnedVersionWithIgnoreScripts));
        fixture.InstallQoderSeed(SeedVersion);
        fixture.Configure(QoderInstallScenario(SeedVersion, "qoder-current"));
        using var runtime = fixture.CreateQoderRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(SeedVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.QoderActivePointerFile).Trim());

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(1, invocations);
        Assert.AreEqual("install", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, $"@qoder-ai/qodercli@{SeedVersion}");
        CollectionAssert.Contains(invocations[0].Arguments, "--save-exact");
        CollectionAssert.Contains(invocations[0].Arguments, "--omit=dev");
        CollectionAssert.Contains(invocations[0].Arguments, "--ignore-scripts");
    }

    [TestMethod]
    public async Task Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer));
        fixture.InstallQoderSeed(SeedVersion);
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        fixture.Configure(QoderViewScenario("1.2.0"), QoderInstallScenario("1.2.0", "qoder-next"));
        using var runtime = fixture.CreateQoderRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.QoderActivePointerFile).Trim());
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(SeedVersion, snapshot.CurrentVersion);
        Assert.AreEqual("1.2.0", snapshot.PendingVersion);
        Assert.IsTrue(snapshot.HasPendingUpdate);

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(2, invocations);
        Assert.AreEqual("view", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, "@qoder-ai/qodercli@latest");
        Assert.AreEqual("install", invocations[1].Command);
        CollectionAssert.Contains(invocations[1].Arguments, "@qoder-ai/qodercli@1.2.0");
        CollectionAssert.Contains(invocations[1].Arguments, "--ignore-scripts");
    }

    [TestMethod]
    public async Task Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate));
        fixture.InstallQoderSeed(SeedVersion);
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        fixture.Configure(QoderViewScenario(SeedVersion));
        using var runtime = fixture.CreateQoderRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.QoderActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.QoderNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations());
        Assert.AreEqual("view", fixture.ReadInvocations()[0].Command);
    }

    [TestMethod]
    public async Task Refresh_RegistryOlderThanCurrent_RefusesDowngrade()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_RegistryOlderThanCurrent_RefusesDowngrade));
        fixture.InstallQoderSeed(SeedVersion);
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        fixture.Configure(QoderViewScenario("1.0.0"));
        using var runtime = fixture.CreateQoderRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "低于当前版本");
        Assert.IsFalse(Directory.Exists(fixture.Paths.QoderNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task PrepareForStartup_PointerNext_PromotesToQoderCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_PointerNext_PromotesToQoderCurrent));
        fixture.InstallQoderSeed(SeedVersion);
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        fixture.Configure(QoderViewScenario("1.2.0"), QoderInstallScenario("1.2.0", "qoder-next"));
        using var runtime = fixture.CreateQoderRuntime();
        await runtime.RefreshAsync();

        await runtime.PrepareForStartupAsync();

        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.QoderActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.QoderNextDirectory));
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual("1.2.0", snapshot.CurrentVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);
        StringAssert.StartsWith(spec.Arguments![0], fixture.Paths.QoderCurrentDirectory);
    }

    private static FakeNpmScenario QoderViewScenario(string version) => new()
    {
        Command = "view",
        WorkingDirectoryName = "runtime",
        StandardOutput = "\"" + version + "\"",
        CreateAdapter = false,
        CreateClaude = false
    };

    private static FakeNpmScenario QoderInstallScenario(string version, string workingDirectoryName) => new()
    {
        Command = "install",
        WorkingDirectoryName = workingDirectoryName,
        CreateAdapter = false,
        CreateClaude = false,
        CreateQoder = true,
        QoderVersion = version
    };
}
