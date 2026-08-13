using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ClineAcpAgentProviderTests
{
    [TestMethod]
    public void DescriptorAndRuntime_MatchManagedContract()
    {
        using var fixture = new FakeNpmFixture(nameof(DescriptorAndRuntime_MatchManagedContract));
        using var runtime = fixture.CreateClineRuntime();
        var provider = new ClineAcpAgentProvider(runtime);

        Assert.AreEqual("acp-cline", provider.Descriptor.Key);
        Assert.AreEqual("Cline", provider.Descriptor.DisplayName);
        Assert.AreEqual("Cline", provider.Descriptor.AssistantName);
        Assert.AreEqual("cline", provider.Descriptor.IconKey);
        Assert.IsTrue(provider.Runtime.SupportsSelfUpdate);
        Assert.IsNull(provider.UsageSource);
        Assert.IsNotNull(provider.ConfigSource);
    }

    [TestMethod]
    public void NativeAndLoginProfiles_UseManagedWrapperAndSharedEnvironmentPolicy()
    {
        using var fixture = new FakeNpmFixture(nameof(NativeAndLoginProfiles_UseManagedWrapperAndSharedEnvironmentPolicy));
        fixture.InstallClineSeed();
        fixture.CreateClineInstall(fixture.Paths.ClineCurrentDirectory, ClineAcpRuntime.SeededPackageVersion);
        using var runtime = fixture.CreateClineRuntime();
        var provider = new ClineAcpAgentProvider(runtime);

        var tui = provider.CreateNativeTerminalProfile("C:\\work dir", "session'id");
        var login = provider.CreateLoginTerminalProfile("C:\\work dir");

        Assert.IsNotNull(tui);
        Assert.IsNotNull(login);
        StringAssert.Contains(tui.Arguments, "'--tui'");
        StringAssert.Contains(tui.Arguments, "'--id'");
        StringAssert.Contains(tui.Arguments, "'session''id'");
        StringAssert.Contains(login.Arguments, "'auth'");
        foreach (var arguments in new[] { tui.Arguments, login.Arguments })
        {
            StringAssert.Contains(arguments, "$env:CLINE_NO_AUTO_UPDATE = '1'");
            StringAssert.Contains(arguments, "$env:CLINE_SESSION_BACKEND_MODE = 'local'");
            StringAssert.Contains(arguments, "Remove-Item Env:CLINE_DIR");
            StringAssert.Contains(arguments, "Remove-Item Env:CLINE_DATA_DIR");
            StringAssert.Contains(arguments, "Remove-Item Env:CLINE_HUB_ADDRESS");
        }
    }
}
