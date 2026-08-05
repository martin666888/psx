using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QoderCliAcpAgentProviderTests
{
    [TestMethod]
    public void Descriptor_MatchesExternalQoderContract()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.AreEqual("acp-qoder", provider.Descriptor.Key);
            Assert.AreEqual("Qoder CLI", provider.Descriptor.DisplayName);
            Assert.AreEqual("Qoder", provider.Descriptor.AssistantName);
            Assert.AreEqual("qoder", provider.Descriptor.IconKey);
            Assert.HasCount(0, provider.Descriptor.LegacyKeys);
            Assert.IsNull(provider.UsageSource);
        }
    }

    [TestMethod]
    public void Runtime_IsExternalWithSelfManagedUiHints()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.AreEqual(AcpRuntimeOwnershipKind.External, provider.Runtime.OwnershipKind);
            Assert.IsFalse(provider.Runtime.SupportsSelfUpdate);
            var hints = provider.Runtime.GetExternalUiHints();
            Assert.IsNotNull(hints);
            Assert.AreEqual("https://docs.qoder.com/cli/install", hints!.InstallDocsUrl);
            Assert.AreEqual("npm install -g @qoder-ai/qodercli", hints.InstallCommandHint);
            Assert.AreEqual("外部安装，由 Qoder 管理", hints.OwnershipLabel);
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
    public void CreateNativeTerminalProfile_UsesPlainQoderCliWithoutResume()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            var profile = provider.CreateNativeTerminalProfile("C:/projects/app", "session-9");

            Assert.IsNotNull(profile);
            StringAssert.Contains(profile!.Arguments, "qodercli");
            Assert.IsFalse(profile.Arguments.Contains("--resume", StringComparison.Ordinal));
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
            Assert.IsTrue(provider.ClientCapabilities.SessionBooleanConfig);
            Assert.IsTrue(provider.ClientCapabilities.ElicitationFormUrl);
            Assert.IsTrue(provider.ClientCapabilities.TerminalOutputMeta);
        }
    }

    [TestMethod]
    public void Registry_QoderRegistered_DefaultStaysClaudeAndQoderIsDiscoverable()
    {
        using var workspace = TestWorkspace.Create(nameof(Registry_QoderRegistered_DefaultStaysClaudeAndQoderIsDiscoverable));
        var claude = new TestProvider("acp-claude", "Claude Code", new CountingRuntime(workspace.Path), []);
        var qoder = new QoderCliAcpAgentProvider(
            new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs")));

        var registry = new AgentProviderRegistry(
            [claude, qoder],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        Assert.AreSame(claude, registry.DefaultProvider);
        Assert.AreSame(qoder, registry.Find("acp-qoder"));
        CollectionAssert.AreEqual(new IAcpAgentProvider[] { claude, qoder }, registry.Providers.ToArray());
    }

    private static QoderCliAcpAgentProvider CreateProvider(out TestWorkspace workspace)
    {
        workspace = TestWorkspace.Create("QoderProvider");
        var runtime = new QoderCliAcpRuntime(Path.Combine(workspace.Path, "logs"));
        return new QoderCliAcpAgentProvider(runtime);
    }
}
