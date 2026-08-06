using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// OpenCode two-directory self-update: the lightweight registry pre-check
/// (npm view of the platform package actually being installed), exact-version
/// staging into runtime/opencode-next via the fake npm, the staged validation
/// chain (structure, version, native-exe smoke, pointer), startup promote,
/// the bundled-baseline fallback when a PSX release ships the same or a newer
/// OpenCode — and the AVX2-less machine path, where the bundled modern binary
/// cannot run and the baseline variant is installed / refreshed into
/// runtime/opencode-current instead.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class OpencodeRuntimeUpdateTests
{
    private const string BundledVersion = "1.18.14";

    [TestMethod]
    public async Task Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerVersion_ViewsThenStagesExactVersionAndFlipsPointer));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.Configure(OpencodeViewScenario("1.19.0"), OpencodeInstallScenario("opencode-next", "1.19.0"));
        using var runtime = fixture.CreateOpencodeRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.OpencodeActivePointerFile).Trim());
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(BundledVersion, snapshot.CurrentVersion);
        Assert.AreEqual("1.19.0", snapshot.PendingVersion);
        Assert.IsTrue(snapshot.HasPendingUpdate);
        Assert.IsTrue(runtime.IsReady());

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(2, invocations, "exactly one view followed by one install");
        Assert.AreEqual("view", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, "opencode-windows-x64@latest");
        Assert.AreEqual("install", invocations[1].Command);
        CollectionAssert.Contains(invocations[1].Arguments, "opencode-windows-x64@1.19.0");
        CollectionAssert.Contains(invocations[1].Arguments, "--save-exact");
        CollectionAssert.Contains(invocations[1].Arguments, "--omit=dev");
    }

    [TestMethod]
    public async Task Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_OnlyRunsViewAndReportsUpToDate));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.Configure(OpencodeViewScenario(BundledVersion));
        using var runtime = fixture.CreateOpencodeRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.OpencodeActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.OpencodeNextDirectory));
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        var invocations = fixture.ReadInvocations();
        Assert.HasCount(1, invocations);
        Assert.AreEqual("view", invocations[0].Command);
    }

    [TestMethod]
    public async Task Refresh_RegistryOlderThanCurrent_RefusesDowngrade()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_RegistryOlderThanCurrent_RefusesDowngrade));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.Configure(OpencodeViewScenario("1.17.0"));
        using var runtime = fixture.CreateOpencodeRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "低于当前版本");
        Assert.IsFalse(Directory.Exists(fixture.Paths.OpencodeNextDirectory));
        Assert.HasCount(1, fixture.ReadInvocations(), "no download may run for a downgrade");
    }

    [TestMethod]
    public async Task PrepareForStartup_PointerNext_PromotesToOpencodeCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_PointerNext_PromotesToOpencodeCurrent));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.Configure(OpencodeViewScenario("1.19.0"), OpencodeInstallScenario("opencode-next", "1.19.0"));
        using var runtime = fixture.CreateOpencodeRuntime();
        await runtime.RefreshAsync();

        await runtime.PrepareForStartupAsync();

        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.OpencodeActivePointerFile).Trim());
        Assert.IsFalse(Directory.Exists(fixture.Paths.OpencodeNextDirectory));
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual("1.19.0", snapshot.CurrentVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);
        StringAssert.StartsWith(spec.FileName, fixture.Paths.OpencodeCurrentDirectory);
        Assert.HasCount(1, spec.Arguments!);
        Assert.AreEqual("acp", spec.Arguments![0]);
    }

    [TestMethod]
    public async Task PrepareForStartup_BundledSameOrNewer_DropsRuntimeCopy()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_BundledSameOrNewer_DropsRuntimeCopy));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.CreateOpencodeInstall(fixture.Paths.OpencodeCurrentDirectory, "1.17.0");
        using var runtime = fixture.CreateOpencodeRuntime();
        Assert.AreEqual("1.17.0", runtime.GetVersionSnapshot().CurrentVersion);

        await runtime.PrepareForStartupAsync();

        Assert.IsFalse(Directory.Exists(fixture.Paths.OpencodeCurrentDirectory));
        Assert.AreEqual(BundledVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public async Task PrepareForStartup_NoAvx2_BundledSameOrNewer_KeepsBaselineRuntimeCopy()
    {
        using var fixture = new FakeNpmFixture(nameof(PrepareForStartup_NoAvx2_BundledSameOrNewer_KeepsBaselineRuntimeCopy));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.CreateOpencodeInstall(
            fixture.Paths.OpencodeCurrentDirectory,
            "1.18.0",
            OpencodeAcpRuntime.BaselinePackageName);
        using var runtime = fixture.CreateOpencodeRuntime(supportsAvx2: false);
        Assert.AreEqual("1.18.0", runtime.GetVersionSnapshot().CurrentVersion);

        await runtime.PrepareForStartupAsync();

        Assert.IsTrue(
            Directory.Exists(fixture.Paths.OpencodeCurrentDirectory),
            "the baseline install is the only usable runtime on an AVX2-less machine and must survive startup");
        Assert.AreEqual("1.18.0", runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public async Task EnsureInstalled_NoAvx2_InstallsBaselineVariantIntoCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_NoAvx2_InstallsBaselineVariantIntoCurrent));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.Configure(OpencodeInstallScenario(
            "opencode-current",
            BundledVersion,
            OpencodeAcpRuntime.BaselinePackageName));
        using var runtime = fixture.CreateOpencodeRuntime(supportsAvx2: false);
        Assert.IsFalse(runtime.IsReady(), "the bundled modern binary is unusable without AVX2");

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(BundledVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsTrue(File.Exists(Path.Combine(
            fixture.Paths.OpencodeCurrentDirectory,
            "node_modules",
            OpencodeAcpRuntime.BaselinePackageName,
            "bin",
            "opencode.exe")));

        var invocations = fixture.ReadInvocations();
        Assert.HasCount(1, invocations);
        Assert.AreEqual("install", invocations[0].Command);
        CollectionAssert.Contains(invocations[0].Arguments, "opencode-windows-x64-baseline@1.18.14");
    }

    [TestMethod]
    public async Task Refresh_NoAvx2_ViewsAndInstallsBaselineVariant()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NoAvx2_ViewsAndInstallsBaselineVariant));
        fixture.InstallOpencodeBundle(BundledVersion);
        fixture.CreateOpencodeInstall(
            fixture.Paths.OpencodeCurrentDirectory,
            BundledVersion,
            OpencodeAcpRuntime.BaselinePackageName);
        fixture.Configure(
            OpencodeViewScenario("1.19.0"),
            OpencodeInstallScenario("opencode-next", "1.19.0", OpencodeAcpRuntime.BaselinePackageName));
        using var runtime = fixture.CreateOpencodeRuntime(supportsAvx2: false);

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.OpencodeActivePointerFile).Trim());
        var invocations = fixture.ReadInvocations();
        Assert.HasCount(2, invocations);
        CollectionAssert.Contains(invocations[0].Arguments, "opencode-windows-x64-baseline@latest");
        CollectionAssert.Contains(invocations[1].Arguments, "opencode-windows-x64-baseline@1.19.0");
    }

    [TestMethod]
    public async Task EnsureInstalled_NoAvx2_PinsBundledVersionNotMinimum()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_NoAvx2_PinsBundledVersionNotMinimum));
        // Bundled pins a version NEWER than MinimumCompatibleVersion (1.18.14):
        // the baseline install must follow the bundled pin, not the floor.
        fixture.InstallOpencodeBundle("1.20.0");
        fixture.Configure(OpencodeInstallScenario(
            "opencode-current",
            "1.20.0",
            OpencodeAcpRuntime.BaselinePackageName));
        using var runtime = fixture.CreateOpencodeRuntime(supportsAvx2: false);

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        var invocations = fixture.ReadInvocations();
        Assert.HasCount(1, invocations);
        CollectionAssert.Contains(invocations[0].Arguments, "opencode-windows-x64-baseline@1.20.0");
        Assert.AreEqual("1.20.0", runtime.GetVersionSnapshot().CurrentVersion);
    }

    private static FakeNpmScenario OpencodeViewScenario(string version) => new()
    {
        Command = "view",
        WorkingDirectoryName = "runtime",
        StandardOutput = "\"" + version + "\"",
        CreateAdapter = false,
        CreateClaude = false
    };

    private static FakeNpmScenario OpencodeInstallScenario(
        string workingDirectoryName,
        string version,
        string packageName = OpencodeAcpRuntime.ModernPackageName) => new()
        {
            Command = "install",
            WorkingDirectoryName = workingDirectoryName,
            CreateAdapter = false,
            CreateClaude = false,
            CreateOpencode = true,
            OpencodeVersion = version,
            OpencodePackageName = packageName
        };
}
