using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Kimi Code two-directory self-update: staging into runtime/kimi-next via
/// the fake npm, pointer flips, startup promote, and the bundled-baseline
/// fallback when a PSX release ships the same or a newer Kimi.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class KimiCodeRuntimeUpdateTests
{
    private const string BundledVersion = "0.29.1";

    [TestMethod]
    public async Task Refresh_NewerVersion_StagesKimiNextAndFlipsPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerVersion_StagesKimiNextAndFlipsPointer));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiInstallScenario("0.30.0"));
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.KimiActivePointerFile).Trim());
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(BundledVersion, snapshot.CurrentVersion);
        Assert.AreEqual("0.30.0", snapshot.PendingVersion);
        Assert.IsTrue(snapshot.HasPendingUpdate);
        // The live install stays on the bundled baseline until restart.
        Assert.IsTrue(runtime.IsReady());
        var invocation = fixture.ReadInvocations().Single();
        Assert.AreEqual("install", invocation.Command);
        CollectionAssert.Contains(invocation.Arguments, "@moonshot-ai/kimi-code@latest");
        CollectionAssert.Contains(invocation.Arguments, "--include=optional");
    }

    [TestMethod]
    public async Task Refresh_SameVersion_ReportsUpToDateAndClearsStaging()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_ReportsUpToDateAndClearsStaging));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiInstallScenario(BundledVersion));
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.KimiActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.KimiNextDirectory));
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
    }

    [TestMethod]
    public async Task PrepareForStartup_PointerNext_PromotesToKimiCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_PointerNext_PromotesToKimiCurrent));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiInstallScenario("0.30.0"));
        using var runtime = fixture.CreateKimiRuntime();
        await runtime.RefreshAsync();

        await runtime.PrepareForStartupAsync();

        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.KimiActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.KimiNextDirectory));
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual("0.30.0", snapshot.CurrentVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        // The process spec now launches the self-updated entry point.
        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);
        StringAssert.StartsWith(spec.Arguments![0], fixture.Paths.KimiCurrentDirectory);
    }

    [TestMethod]
    public async Task PrepareForStartup_BundledSameOrNewer_DropsRuntimeCopy()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_BundledSameOrNewer_DropsRuntimeCopy));
        fixture.InstallKimiBundle(BundledVersion);
        // Simulate a stale self-update from before a PSX upgrade.
        fixture.CreateKimiInstall(fixture.Paths.KimiCurrentDirectory, "0.28.0");
        using var runtime = fixture.CreateKimiRuntime();
        Assert.AreEqual("0.28.0", runtime.GetVersionSnapshot().CurrentVersion);

        await runtime.PrepareForStartupAsync();

        Assert.IsFalse(Directory.Exists(fixture.Paths.KimiCurrentDirectory));
        Assert.AreEqual(BundledVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(runtime.IsReady());
    }

    private static FakeNpmScenario KimiInstallScenario(string version) => new()
    {
        Command = "install",
        WorkingDirectoryName = "kimi-next",
        CreateAdapter = false,
        CreateClaude = false,
        CreateKimi = true,
        KimiVersion = version
    };
}
