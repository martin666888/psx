using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class OpencodeAcpAgentProviderTests
{
    [TestMethod]
    public void Descriptor_MatchesBundledOpencodeContract()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.AreEqual("acp-opencode", provider.Descriptor.Key);
            Assert.AreEqual("OpenCode", provider.Descriptor.DisplayName);
            Assert.AreEqual("OpenCode", provider.Descriptor.AssistantName);
            Assert.AreEqual("opencode", provider.Descriptor.IconKey);
            Assert.HasCount(0, provider.Descriptor.LegacyKeys);
            Assert.IsNull(provider.UsageSource);
        }
    }

    [TestMethod]
    public void CreateNewSessionParameters_CarriesCwdAndEmptyMcpServers()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            using var json = JsonSerializer.SerializeToDocument(
                provider.CreateNewSessionParameters("C:/projects/app"));
            var root = json.RootElement;

            Assert.AreEqual("C:/projects/app", root.GetProperty("cwd").GetString());
            Assert.AreEqual(JsonValueKind.Array, root.GetProperty("mcpServers").ValueKind);
            Assert.AreEqual(0, root.GetProperty("mcpServers").GetArrayLength());
        }
    }

    [TestMethod]
    public void CreateRestoreSessionParameters_CarriesSessionIdCwdAndEmptyMcpServers()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            using var json = JsonSerializer.SerializeToDocument(
                provider.CreateRestoreSessionParameters("session-123", "C:/projects/app"));
            var root = json.RootElement;

            Assert.AreEqual("session-123", root.GetProperty("sessionId").GetString());
            Assert.AreEqual("C:/projects/app", root.GetProperty("cwd").GetString());
            Assert.AreEqual(JsonValueKind.Array, root.GetProperty("mcpServers").ValueKind);
            Assert.AreEqual(0, root.GetProperty("mcpServers").GetArrayLength());
        }
    }

    [TestMethod]
    public void IsCommandVisible_ShowsEveryCommandInV1()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.IsTrue(provider.IsCommandVisible("anything"));
            Assert.IsTrue(provider.IsCommandVisible("/terminal"));
        }
    }

    [TestMethod]
    public void SessionBudgets_WiderTimeoutWithoutAuthConfusion()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(60), provider.NewSessionTimeout);
            Assert.IsFalse(((IAcpAgentProvider)provider).TreatNewSessionTimeoutAsAuthRequired);
        }
    }

    [TestMethod]
    public void ClientCapabilities_AdvertisesFullReverseFilesystemBridge()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.IsTrue(provider.ClientCapabilities.FileSystemReadText);
            Assert.IsTrue(provider.ClientCapabilities.FileSystemWriteText);
            Assert.IsTrue(provider.ClientCapabilities.Terminal);
        }
    }

    [TestMethod]
    public void CreateNativeTerminalProfile_UsesManagedExeWithSessionFlag()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateNativeTerminalProfile_UsesManagedExeWithSessionFlag));
        fixture.InstallOpencodeBundle();
        var provider = new OpencodeAcpAgentProvider(fixture.CreateOpencodeRuntime());

        var profile = provider.CreateNativeTerminalProfile("C:/projects/app", "session-9");

        Assert.IsNotNull(profile);
        StringAssert.Contains(profile!.Arguments, "opencode.exe");
        StringAssert.Contains(profile.Arguments, "--session");
        StringAssert.Contains(profile.Arguments, "session-9");
        Assert.IsFalse(profile.Arguments.Contains("--acp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateNativeTerminalProfile_IncompleteInstall_ReturnsNull()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.IsNull(provider.CreateNativeTerminalProfile("C:/projects/app", "session-9"));
        }
    }

    [TestMethod]
    public void CreateLoginTerminalProfile_UsesAuthLoginTuiNotAcpLoginFlag()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateLoginTerminalProfile_UsesAuthLoginTuiNotAcpLoginFlag));
        fixture.InstallOpencodeBundle();
        var provider = new OpencodeAcpAgentProvider(fixture.CreateOpencodeRuntime());

        var profile = provider.CreateLoginTerminalProfile("C:/projects/app");

        Assert.IsNotNull(profile);
        StringAssert.Contains(profile!.Arguments, "opencode.exe");
        StringAssert.Contains(profile.Arguments, "auth login");
        Assert.IsFalse(profile.Arguments.Contains("--acp", StringComparison.Ordinal));
        Assert.IsFalse(profile.Arguments.Contains("--login", StringComparison.Ordinal));
        Assert.IsFalse(profile.Arguments.Contains("--session", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateLoginTerminalProfile_IncompleteInstall_ReturnsNull()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.IsNull(provider.CreateLoginTerminalProfile("C:/projects/app"));
        }
    }

    private static OpencodeAcpAgentProvider CreateProvider(out TestWorkspace workspace)
    {
        workspace = TestWorkspace.Create("OpencodeProvider");
        var runtime = new OpencodeAcpRuntime(
            new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        return new OpencodeAcpAgentProvider(runtime);
    }
}
