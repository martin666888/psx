using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QwenCodeAcpAgentProviderTests
{
    [TestMethod]
    public void Descriptor_MatchesBundledQwenContract()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.AreEqual("acp-qwen", provider.Descriptor.Key);
            Assert.AreEqual("Qwen Code", provider.Descriptor.DisplayName);
            Assert.AreEqual("Qwen", provider.Descriptor.AssistantName);
            Assert.AreEqual("qwen", provider.Descriptor.IconKey);
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
    public void CreateNativeTerminalProfile_ResumesWithSessionId()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            var profile = provider.CreateNativeTerminalProfile("C:/projects/app", "session-9");

            Assert.IsNotNull(profile);
            StringAssert.Contains(profile!.Arguments, "qwen --resume session-9");
        }
    }

    [TestMethod]
    public void CreateLoginTerminalProfile_UsesInteractiveTuiNotAcpLoginFlag()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateLoginTerminalProfile_UsesInteractiveTuiNotAcpLoginFlag));
        fixture.InstallQwenBundle();
        var provider = new QwenCodeAcpAgentProvider(fixture.CreateQwenRuntime());

        var profile = provider.CreateLoginTerminalProfile("C:/projects/app");

        Assert.IsNotNull(profile);
        StringAssert.Contains(profile!.Arguments, "node.exe");
        StringAssert.Contains(profile.Arguments, "cli-entry.js");
        Assert.IsFalse(profile.Arguments.Contains("--acp", StringComparison.Ordinal));
        Assert.IsFalse(profile.Arguments.Contains("--login", StringComparison.Ordinal));
        Assert.IsFalse(profile.Arguments.Contains("--resume", StringComparison.Ordinal));
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

    [TestMethod]
    public void Registry_QwenRegistered_DefaultStaysClaudeAndQwenIsDiscoverable()
    {
        using var workspace = TestWorkspace.Create(nameof(Registry_QwenRegistered_DefaultStaysClaudeAndQwenIsDiscoverable));
        var claude = new TestProvider("acp-claude", "Claude Code", new CountingRuntime(workspace.Path), []);
        var qwen = new QwenCodeAcpAgentProvider(
            new QwenCodeAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs")));

        var registry = new AgentProviderRegistry(
            [claude, qwen],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        Assert.AreSame(claude, registry.DefaultProvider);
        Assert.AreSame(qwen, registry.Find("acp-qwen"));
        CollectionAssert.AreEqual(new IAcpAgentProvider[] { claude, qwen }, registry.Providers.ToArray());
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

    private static QwenCodeAcpAgentProvider CreateProvider(out TestWorkspace workspace)
    {
        workspace = TestWorkspace.Create("QwenProvider");
        var runtime = new QwenCodeAcpRuntime(
            new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        return new QwenCodeAcpAgentProvider(runtime);
    }
}
