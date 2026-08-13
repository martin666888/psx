using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ClineAcpRuntimeTests
{
    [TestMethod]
    public void IsReady_RequiresMatchingWrapperAndPlatform()
    {
        using var fixture = new FakeNpmFixture(nameof(IsReady_RequiresMatchingWrapperAndPlatform));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.53", "3.0.52");
        using var runtime = fixture.CreateClineRuntime();

        Assert.IsFalse(runtime.IsReady());

        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.53", "3.0.53");
        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_MatchingBelowMinimum_IsFalse()
    {
        using var fixture = new FakeNpmFixture(nameof(IsReady_MatchingBelowMinimum_IsFalse));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.0.52", "3.0.52");
        using var runtime = fixture.CreateClineRuntime();

        Assert.IsFalse(runtime.IsReady());
        Assert.AreEqual("3.0.52", runtime.GetVersionSnapshot().CurrentVersion);
    }

    [TestMethod]
    public void IsReady_MatchingNewerThanSeed_IsTrue()
    {
        using var fixture = new FakeNpmFixture(nameof(IsReady_MatchingNewerThanSeed_IsTrue));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, "3.1.0", "3.1.0");
        using var runtime = fixture.CreateClineRuntime();

        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual("3.1.0", runtime.GetVersionSnapshot().CurrentVersion);
        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);
        StringAssert.StartsWith(spec.Arguments[0], fixture.Paths.ClineCurrentDirectory);
    }

    [TestMethod]
    public void CreateProcessSpec_UsesWrapperLocalBackendAndClearsAmbientOverrides()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateProcessSpec_UsesWrapperLocalBackendAndClearsAmbientOverrides));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, ClineAcpRuntime.SeededPackageVersion);
        using var runtime = fixture.CreateClineRuntime();

        var spec = runtime.CreateProcessSpec(fixture.InstallDirectory);

        Assert.AreEqual(fixture.Paths.PortableNodePath, spec.FileName);
        CollectionAssert.AreEqual(
            new[] { spec.Arguments[0], "--acp", "--auto-approve", "false" },
            spec.Arguments.ToArray());
        StringAssert.Contains(spec.Arguments[0], Path.Combine("node_modules", "cline", "bin"));
        Assert.AreEqual("1", spec.Environment["CLINE_NO_AUTO_UPDATE"]);
        Assert.AreEqual("local", spec.Environment["CLINE_SESSION_BACKEND_MODE"]);
        StringAssert.EndsWith(spec.Environment["CLINE_BIN_PATH"]!, Path.Combine("bin", "cline.exe"));
        foreach (var name in new[]
                 {
                     "CLINE_PROVIDER", "CLINE_MODEL", "CLINE_HUB_ADDRESS",
                     "CLINE_DATA_DIR", "CLINE_DIR", "CLINE_API_KEY"
                 })
        {
            Assert.IsTrue(spec.Environment.ContainsKey(name));
            Assert.IsNull(spec.Environment[name]);
        }
        Assert.IsFalse(spec.Environment.ContainsKey("NODE_EXTRA_CA_CERTS"));
        Assert.IsFalse(spec.Environment.ContainsKey("PATH"));
    }

    [TestMethod]
    public async Task RefreshAsync_WhenNotInstalled_SkipsWithoutNpm()
    {
        using var fixture = new FakeNpmFixture(nameof(RefreshAsync_WhenNotInstalled_SkipsWithoutNpm));
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        StringAssert.Contains(result.Message, "not installed");
        Assert.HasCount(0, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task RefreshAsync_WithoutPortableNpm_FailsWithoutTouchingInstall()
    {
        using var fixture = new FakeNpmFixture(nameof(RefreshAsync_WithoutPortableNpm_FailsWithoutTouchingInstall));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, ClineAcpRuntime.SeededPackageVersion);
        File.Delete(fixture.Paths.PortableNpmCliPath!);
        using var runtime = fixture.CreateClineRuntime();

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(ClineAcpRuntime.SeededPackageVersion, runtime.GetVersionSnapshot().CurrentVersion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.ClineNextDirectory));
        Assert.HasCount(0, fixture.ReadInvocations());
    }
}
