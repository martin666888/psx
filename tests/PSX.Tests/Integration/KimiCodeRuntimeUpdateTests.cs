using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Kimi Code two-directory self-update: the lightweight registry pre-check
/// (npm view), exact-version staging into runtime/kimi-next via the fake npm,
/// the staged validation chain (structure, version, smoke, pointer), startup
/// promote, and the bundled-baseline fallback when a PSX release ships the
/// same or a newer Kimi.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class KimiCodeRuntimeUpdateTests
{
    private const string BundledVersion = "0.29.1";

    [TestMethod]
    public async Task Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiViewScenario("0.30.0"), KimiInstallScenario("0.30.0"));
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

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(2, invocations, "exactly one view followed by one install");
        Assert.AreEqual("view", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, "@moonshot-ai/kimi-code@latest");
        Assert.AreEqual("install", invocations[1].Command);
        // The install pins the exact version the pre-check saw and hardens
        // the run: exact save, no dev deps, optional prebuilts, engine gate.
        CollectionAssert.Contains(invocations[1].Arguments, "@moonshot-ai/kimi-code@0.30.0");
        CollectionAssert.Contains(invocations[1].Arguments, "--save-exact");
        CollectionAssert.Contains(invocations[1].Arguments, "--omit=dev");
        CollectionAssert.Contains(invocations[1].Arguments, "--include=optional");
        CollectionAssert.Contains(invocations[1].Arguments, "--engine-strict");
    }

    [TestMethod]
    public async Task Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiViewScenario(BundledVersion));
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.KimiActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.KimiNextDirectory));
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        // The whole check cost one lightweight view; no ~100 MB install ran.
        var invocations = fixture.ReadInvocations();
        Assert.HasCount(1, invocations);
        Assert.AreEqual("view", invocations[0].Command);
    }

    [TestMethod]
    public async Task Refresh_RegistryOlderThanCurrent_RefusesDowngrade()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_RegistryOlderThanCurrent_RefusesDowngrade));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiViewScenario("0.28.0"));
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "低于当前版本");
        Assert.IsFalse(Directory.Exists(fixture.Paths.KimiNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations(), "no download may run for a downgrade");
    }

    [TestMethod]
    public async Task Refresh_UnparseableRegistryVersion_FailsWithoutDownloading()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_UnparseableRegistryVersion_FailsWithoutDownloading));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiViewScenario("0.31.0-beta.1"));
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        // Failed, never AlreadyReady: the coordinator maps AlreadyReady to
        // "Up to date", which would disguise "cannot tell" as "newest".
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        StringAssert.Contains(result.Message, "无法安全比较");
        Assert.IsFalse(Directory.Exists(fixture.Paths.KimiNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations());
        Assert.IsTrue(runtime.IsReady(), "the active install must stay untouched");
    }

    [TestMethod]
    public async Task Refresh_StagedVersionMismatch_DoesNotFlipPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_StagedVersionMismatch_DoesNotFlipPointer));
        fixture.InstallKimiBundle(BundledVersion);
        // npm reports 0.30.0 but the install writes 0.29.9: the staged tree
        // is not what was requested and must never be activated.
        fixture.Configure(KimiViewScenario("0.30.0"), KimiInstallScenario("0.29.9"));
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        StringAssert.Contains(result.Message, "does not match");
        Assert.AreNotEqual("next", ReadPointerOrEmpty(fixture));
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
    }

    [TestMethod]
    public async Task Refresh_SmokeCheckFails_DoesNotFlipPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SmokeCheckFails_DoesNotFlipPointer));
        fixture.InstallKimiBundle(BundledVersion);
        var install = KimiInstallScenario("0.30.0");
        install.KimiSmokeFails = true;
        fixture.Configure(KimiViewScenario("0.30.0"), install);
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        // Files installed fine but the entry cannot start: never activate it.
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        StringAssert.Contains(result.Message, "start check");
        Assert.AreNotEqual("next", ReadPointerOrEmpty(fixture));
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public async Task Refresh_PointerWriteFails_ReportsFailedInsteadOfUpToDate()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_PointerWriteFails_ReportsFailedInsteadOfUpToDate));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiViewScenario("0.30.0"), KimiInstallScenario("0.30.0"));
        // A directory squatting on the pointer path makes the atomic
        // move fail, which used to be swallowed silently.
        Directory.CreateDirectory(fixture.Paths.KimiActivePointerFile);
        using var runtime = fixture.CreateKimiRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        StringAssert.Contains(result.Message, "激活指针");
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        Assert.IsTrue(runtime.IsReady(), "the bundled baseline must remain the active install");
    }

    [TestMethod]
    public async Task PrepareForStartup_PointerNext_PromotesToKimiCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_PointerNext_PromotesToKimiCurrent));
        fixture.InstallKimiBundle(BundledVersion);
        fixture.Configure(KimiViewScenario("0.30.0"), KimiInstallScenario("0.30.0"));
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

    private static string ReadPointerOrEmpty(FakeNpmFixture fixture) =>
        File.Exists(fixture.Paths.KimiActivePointerFile)
            ? File.ReadAllText(fixture.Paths.KimiActivePointerFile).Trim()
            : "";

    /// <summary>npm view prints the version as a JSON string.</summary>
    private static FakeNpmScenario KimiViewScenario(string version) => new()
    {
        Command = "view",
        WorkingDirectoryName = "runtime",
        StandardOutput = "\"" + version + "\"",
        CreateAdapter = false,
        CreateClaude = false
    };

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
