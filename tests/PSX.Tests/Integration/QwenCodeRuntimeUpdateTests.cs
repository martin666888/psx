using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Qwen Code two-directory self-update: the lightweight registry pre-check
/// (npm view), exact-version staging into runtime/qwen-next via the fake npm,
/// the staged validation chain (structure, version, smoke, pointer), startup
/// promote, and the bundled-baseline fallback when a PSX release ships the
/// same or a newer Qwen.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class QwenCodeRuntimeUpdateTests
{
    private const string BundledVersion = "0.21.5";

    [TestMethod]
    public async Task Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer));
        fixture.InstallQwenBundle(BundledVersion);
        fixture.Configure(QwenViewScenario("0.22.0"), QwenInstallScenario("0.22.0"));
        using var runtime = fixture.CreateQwenRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.QwenActivePointerFile).Trim());
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(BundledVersion, snapshot.CurrentVersion);
        Assert.AreEqual("0.22.0", snapshot.PendingVersion);
        Assert.IsTrue(snapshot.HasPendingUpdate);
        Assert.IsTrue(runtime.IsReady());

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(2, invocations, "exactly one view followed by one install");
        Assert.AreEqual("view", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, "@qwen-code/qwen-code@latest");
        Assert.AreEqual("install", invocations[1].Command);
        CollectionAssert.Contains(invocations[1].Arguments, "@qwen-code/qwen-code@0.22.0");
        CollectionAssert.Contains(invocations[1].Arguments, "--save-exact");
        CollectionAssert.Contains(invocations[1].Arguments, "--omit=dev");
        CollectionAssert.Contains(invocations[1].Arguments, "--include=optional");
        CollectionAssert.Contains(invocations[1].Arguments, "--engine-strict");
    }

    [TestMethod]
    public async Task Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate));
        fixture.InstallQwenBundle(BundledVersion);
        fixture.Configure(QwenViewScenario(BundledVersion));
        using var runtime = fixture.CreateQwenRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.QwenActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.QwenNextDirectory));
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        var invocations = fixture.ReadInvocations();
        Assert.HasCount(1, invocations);
        Assert.AreEqual("view", invocations[0].Command);
    }

    [TestMethod]
    public async Task Refresh_RegistryOlderThanCurrent_RefusesDowngrade()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_RegistryOlderThanCurrent_RefusesDowngrade));
        fixture.InstallQwenBundle(BundledVersion);
        fixture.Configure(QwenViewScenario("0.20.0"));
        using var runtime = fixture.CreateQwenRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "低于当前版本");
        Assert.IsFalse(Directory.Exists(fixture.Paths.QwenNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations(), "no download may run for a downgrade");
    }

    [TestMethod]
    public async Task PrepareForStartup_PointerNext_PromotesToQwenCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_PointerNext_PromotesToQwenCurrent));
        fixture.InstallQwenBundle(BundledVersion);
        fixture.Configure(QwenViewScenario("0.22.0"), QwenInstallScenario("0.22.0"));
        using var runtime = fixture.CreateQwenRuntime();
        await runtime.RefreshAsync();

        await runtime.PrepareForStartupAsync();

        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.QwenActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.QwenNextDirectory));
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual("0.22.0", snapshot.CurrentVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);
        StringAssert.StartsWith(spec.Arguments![0], fixture.Paths.QwenCurrentDirectory);
    }

    [TestMethod]
    public async Task PrepareForStartup_BundledSameOrNewer_DropsRuntimeCopy()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_BundledSameOrNewer_DropsRuntimeCopy));
        fixture.InstallQwenBundle(BundledVersion);
        fixture.CreateQwenInstall(fixture.Paths.QwenCurrentDirectory, "0.20.0");
        using var runtime = fixture.CreateQwenRuntime();
        Assert.AreEqual("0.20.0", runtime.GetVersionSnapshot().CurrentVersion);

        await runtime.PrepareForStartupAsync();

        Assert.IsFalse(Directory.Exists(fixture.Paths.QwenCurrentDirectory));
        Assert.AreEqual(BundledVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(runtime.IsReady());
    }

    private static FakeNpmScenario QwenViewScenario(string version) => new()
    {
        Command = "view",
        WorkingDirectoryName = "runtime",
        StandardOutput = "\"" + version + "\"",
        CreateAdapter = false,
        CreateClaude = false
    };

    private static FakeNpmScenario QwenInstallScenario(string version) => new()
    {
        Command = "install",
        WorkingDirectoryName = "qwen-next",
        CreateAdapter = false,
        CreateClaude = false,
        CreateQwen = true,
        QwenVersion = version
    };
}
