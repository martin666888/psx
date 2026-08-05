using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QoderCliAcpRuntimeTests
{
    private const string SeedVersion = QoderCliAcpRuntime.SeededPackageVersion;

    [TestMethod]
    public void IsReady_WithoutInstall_IsFalse()
    {
        using var fixture = new FakeNpmFixture(nameof(IsReady_WithoutInstall_IsFalse));
        using var runtime = fixture.CreateQoderRuntime();

        Assert.IsFalse(runtime.IsReady());
        Assert.IsNull(runtime.GetVersionSnapshot().CurrentVersion);
        StringAssert.Contains(runtime.BuildStatusText(), "未安装");
    }

    [TestMethod]
    public void IsReady_WithValidCurrentInstall_IsTrue()
    {
        using var fixture = new FakeNpmFixture(nameof(IsReady_WithValidCurrentInstall_IsTrue));
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        using var runtime = fixture.CreateQoderRuntime();

        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(SeedVersion, runtime.GetVersionSnapshot().CurrentVersion);
    }

    [TestMethod]
    public void CreateProcessSpec_UsesPortableNodePlusBundleEntryAndAcp()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateProcessSpec_UsesPortableNodePlusBundleEntryAndAcp));
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        using var runtime = fixture.CreateQoderRuntime();

        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);

        Assert.AreEqual(fixture.Paths.PortableNodePath, spec.FileName);
        Assert.HasCount(2, spec.Arguments);
        StringAssert.Contains(spec.Arguments![0], Path.Combine("node_modules", "@qoder-ai", "qodercli"));
        StringAssert.EndsWith(spec.Arguments[0], "qodercli.js");
        Assert.AreEqual("--acp", spec.Arguments[1]);
    }

    [TestMethod]
    public void TryBuildInteractivePowerShellInvocation_LoginUsesManagedEntry()
    {
        using var fixture = new FakeNpmFixture(nameof(TryBuildInteractivePowerShellInvocation_LoginUsesManagedEntry));
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        using var runtime = fixture.CreateQoderRuntime();

        var login = runtime.TryBuildInteractivePowerShellInvocation("login");

        Assert.IsNotNull(login);
        StringAssert.Contains(login!, fixture.Paths.PortableNodePath!);
        StringAssert.Contains(login, "qodercli.js");
        StringAssert.Contains(login, "'login'");
        Assert.IsFalse(login.Contains("--acp", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EnsureInstalledAsync_WhenAlreadyReady_ReturnsAlreadyReady()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalledAsync_WhenAlreadyReady_ReturnsAlreadyReady));
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        using var runtime = fixture.CreateQoderRuntime();

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.HasCount(0, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task RefreshAsync_WhenNotInstalled_SkipsWithoutNpm()
    {
        using var fixture = new FakeNpmFixture(nameof(RefreshAsync_WhenNotInstalled_SkipsWithoutNpm));
        fixture.InstallQoderSeed();
        using var runtime = fixture.CreateQoderRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "not installed");
        Assert.HasCount(0, fixture.ReadInvocations());
    }
}
