using PSX.Services;
using PSX.Tests.Support;
using PSX.Tests.Unit;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class PiRuntimeInstallTests
{
    [TestMethod]
    public async Task Install_ConfirmedInstallUsesLockedSeedAndNotifiesReady()
    {
        using var fixture = new FakeNpmFixture(nameof(PiRuntimeInstallTests));
        Seed(fixture);
        fixture.Configure(new FakeNpmScenario { Command = "ci", CreatePi = true });
        using var runtime = new PiAcpRuntime(fixture.Locator, Path.Combine(fixture.InstallDirectory, "logs"));
        var ready = false;
        runtime.StatusChanged += _ => ready |= runtime.IsReady();
        var result = await runtime.EnsureInstalledAsync();
        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.IsTrue(ready);
        CollectionAssert.Contains(fixture.ReadInvocations().Single().Arguments, "--engine-strict");
        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, (await runtime.EnsureInstalledAsync()).Kind);
    }

    [TestMethod]
    public async Task Install_FailedSmokeDoesNotLeaveAPromotableMarker()
    {
        using var fixture = new FakeNpmFixture(nameof(PiRuntimeInstallTests));
        Seed(fixture);
        fixture.Configure(new FakeNpmScenario { Command = "ci", CreatePi = true, PiSmokeFails = true });
        using var runtime = new PiAcpRuntime(fixture.Locator, Path.Combine(fixture.InstallDirectory, "logs"));
        Directory.CreateDirectory(fixture.Paths.RuntimeRoot);
        File.WriteAllText(runtime.PointerFile, "next");
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, (await runtime.EnsureInstalledAsync()).Kind);
        await runtime.PrepareForStartupAsync();
        Assert.IsFalse(runtime.IsReady());
        Assert.AreEqual("current", File.ReadAllText(runtime.PointerFile));
    }

    [TestMethod]
    public async Task Refresh_StagesBothExactVersionsAndKeepsLiveTree()
    {
        using var fixture = new FakeNpmFixture(nameof(PiRuntimeInstallTests));
        Seed(fixture);
        fixture.Configure(
            new FakeNpmScenario { Command = "view", StandardOutput = "0.85.2", CreateAdapter = false, CreateClaude = false },
            new FakeNpmScenario { Command = "install", CreatePi = true, PiVersion = "0.85.2", PiAdapterVersion = "0.85.2" });
        using var runtime = new PiAcpRuntime(fixture.Locator, Path.Combine(fixture.InstallDirectory, "logs"));
        PiProviderTests.Install(runtime.CurrentDirectory);
        Assert.AreEqual(AcpRuntimeOperationKind.Success, (await runtime.RefreshAsync()).Kind);
        Assert.AreEqual("0.85.1", runtime.GetVersionSnapshot().CurrentVersion);
        Assert.AreEqual("0.85.2", runtime.GetVersionSnapshot().PendingVersion);
        Assert.IsTrue(runtime.IsReady());
        var install = fixture.ReadInvocations().Single(item => item.Command == "install");
        CollectionAssert.Contains(install.Arguments, "@earendil-works/pi-coding-agent@0.85.2");
        await runtime.PrepareForStartupAsync();
        Assert.AreEqual("0.85.2", runtime.GetVersionSnapshot().CurrentVersion);
    }

    [TestMethod]
    public async Task Install_CancellationRemainsCancelled()
    {
        using var fixture = new FakeNpmFixture(nameof(PiRuntimeInstallTests));
        Seed(fixture);
        fixture.Configure(new FakeNpmScenario { Command = "ci", Hang = true });
        using var runtime = new PiAcpRuntime(fixture.Locator, Path.Combine(fixture.InstallDirectory, "logs"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.AreEqual(AcpRuntimeOperationKind.Cancelled, (await runtime.EnsureInstalledAsync(cancellation.Token)).Kind);
        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public async Task Refresh_UnexpectedInstalledVersionIsNotActivated()
    {
        using var fixture = new FakeNpmFixture(nameof(PiRuntimeInstallTests));
        Seed(fixture);
        fixture.Configure(
            new FakeNpmScenario { Command = "view", StandardOutput = "0.85.2", CreateAdapter = false, CreateClaude = false },
            new FakeNpmScenario { Command = "install", CreatePi = true });
        using var runtime = new PiAcpRuntime(fixture.Locator, Path.Combine(fixture.InstallDirectory, "logs"));
        PiProviderTests.Install(runtime.CurrentDirectory);
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, (await runtime.RefreshAsync()).Kind);
        Assert.AreEqual("current", File.ReadAllText(runtime.PointerFile));
        Assert.AreEqual("0.85.1", runtime.GetVersionSnapshot().CurrentVersion);
    }

    private static void Seed(FakeNpmFixture fixture)
    {
        foreach (var name in new[] { "package.json", "package-lock.json", ".npmrc" })
            PiProviderTests.Write(fixture.InstallDirectory, "tools/pi-seed/" + name,
                File.ReadAllText(Path.Combine(TestWorkspace.RepositoryRoot, "tools", "pi-seed", name)));
        PiProviderTests.Write(fixture.InstallDirectory, "tools/pi-launcher/launch.mjs", "fake");
    }
}
