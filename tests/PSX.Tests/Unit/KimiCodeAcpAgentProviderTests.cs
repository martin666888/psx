using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiCodeAcpAgentProviderTests
{
    [TestMethod]
    public void Descriptor_MatchesBundledKimiContract()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            Assert.AreEqual("acp-kimi", provider.Descriptor.Key);
            Assert.AreEqual("Kimi Code", provider.Descriptor.DisplayName);
            Assert.AreEqual("Kimi", provider.Descriptor.AssistantName);
            Assert.AreEqual("kimi", provider.Descriptor.IconKey);
            Assert.HasCount(0, provider.Descriptor.LegacyKeys);
            // Kimi usage is owned by the local-all contributor; the provider
            // itself no longer carries a PSX-session usage source.
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
            StringAssert.Contains(profile!.Arguments, "kimi --resume session-9");
        }
    }

    [TestMethod]
    public void Registry_ClaudeAndKimiRegistered_DefaultStaysClaudeAndKimiIsDiscoverable()
    {
        using var workspace = TestWorkspace.Create(nameof(Registry_ClaudeAndKimiRegistered_DefaultStaysClaudeAndKimiIsDiscoverable));
        var claude = new TestProvider("acp-claude", "Claude Code", new CountingRuntime(workspace.Path), []);
        var kimi = new KimiCodeAcpAgentProvider(
            new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs")));

        var registry = new AgentProviderRegistry(
            [claude, kimi],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        Assert.AreSame(claude, registry.DefaultProvider);
        Assert.AreSame(kimi, registry.Find("acp-kimi"));
        CollectionAssert.AreEqual(new IAcpAgentProvider[] { claude, kimi }, registry.Providers.ToArray());
    }

    [TestMethod]
    public async Task ProviderCatalog_ContainsKimiAsNonDefaultAlongsideClaude()
    {
        using var workspace = TestWorkspace.Create(nameof(ProviderCatalog_ContainsKimiAsNonDefaultAlongsideClaude));
        var claude = new TestProvider("acp-claude", "Claude Code", new CountingRuntime(workspace.Path), []);
        var kimi = new KimiCodeAcpAgentProvider(
            new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs")));
        var registry = new AgentProviderRegistry(
            [claude, kimi],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        using var history = new AgentHistoryCatalog();
        var tabs = new NullTabManagementService();
        var terminalBridge = new NullTerminalBridgeService();
        var directoryPicker = new NullAgentDirectoryPicker();
        var factory = new AgentWorkspaceFactory(
            bridge, tabs, terminalBridge, store, directoryPicker, registry,
            new AgentRuntimeCoordinator(registry));
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var catalog = coordinator.ProviderCatalog;

        var kimiItem = catalog.SingleOrDefault(item => item.Key == "acp-kimi");
        Assert.IsNotNull(kimiItem);
        Assert.AreEqual("Kimi Code", kimiItem!.DisplayName);
        Assert.IsFalse(kimiItem.IsDefault);
        Assert.IsTrue(catalog.Single(item => item.Key == "acp-claude").IsDefault);
        // The catalog carries each provider's brand icon key; providers that
        // never set one fall back to the generic "agent" mark.
        Assert.AreEqual("kimi", kimiItem.IconKey);
        Assert.AreEqual("agent", catalog.Single(item => item.Key == "acp-claude").IconKey);

        await coordinator.ShutdownAsync();
    }

    [TestMethod]
    public void ClientCapabilities_OmitsFilesystemBridgeButKeepsTerminal()
    {
        var provider = CreateProvider(out var workspace);
        using (workspace)
        {
            // Kimi Code 0.29.1's reverse fs bridge can hang, so Kimi must never
            // advertise it; terminal stays on for standard ACP terminal-auth.
            Assert.IsFalse(provider.ClientCapabilities.FileSystemReadText);
            Assert.IsFalse(provider.ClientCapabilities.FileSystemWriteText);
            Assert.IsTrue(provider.ClientCapabilities.Terminal);
        }
    }

    [TestMethod]
    public void ClaudeClientCapabilities_AdvertisesTheFullReverseFilesystemBridge()
    {
        using var workspace = TestWorkspace.Create(nameof(ClaudeClientCapabilities_AdvertisesTheFullReverseFilesystemBridge));
        using var runtime = new AcpRuntimeManager(
            new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        var claude = new ClaudeAcpAgentProvider(runtime);

        // Claude keeps the full client surface, including fs read/write, so this
        // guards against a provider-agnostic change accidentally dropping it.
        Assert.AreEqual("claude", claude.Descriptor.IconKey);
        Assert.IsTrue(claude.ClientCapabilities.FileSystemReadText);
        Assert.IsTrue(claude.ClientCapabilities.FileSystemWriteText);
        Assert.IsTrue(claude.ClientCapabilities.Terminal);
    }

    [TestMethod]
    public void Descriptor_WithoutBrandIcon_UsesGenericAgentFallback()
    {
        var descriptor = new AgentDescriptor("custom", "Custom", "Custom", []);

        Assert.AreEqual("agent", descriptor.IconKey);
    }

    [TestMethod]
    public void BuildClientCapabilities_OmitsFsKeyWhenAProviderOptsOut()
    {
        var capabilities = AcpAgentSessionService.BuildClientCapabilities(
            new KimiCodeAcpAgentProvider(new KimiCodeAcpRuntime(
                new RuntimeLocator(Path.GetTempPath()), Path.Combine(Path.GetTempPath(), "psx-kimi-logs")))
                .ClientCapabilities);

        Assert.IsFalse(capabilities.ContainsKey("fs"), "Kimi must not advertise the reverse fs bridge.");
        Assert.IsTrue(capabilities.ContainsKey("terminal"));
    }

    [TestMethod]
    public void BuildClientCapabilities_IncludesFsKeyWhenAProviderOptsIn()
    {
        var capabilities = AcpAgentSessionService.BuildClientCapabilities(new AcpClientCapabilityProfile
        {
            FileSystemReadText = true,
            FileSystemWriteText = true,
            Terminal = true
        });

        Assert.IsTrue(capabilities.ContainsKey("fs"));
        using var json = JsonSerializer.SerializeToDocument(capabilities["fs"]);
        Assert.IsTrue(json.RootElement.GetProperty("readTextFile").GetBoolean());
        Assert.IsTrue(json.RootElement.GetProperty("writeTextFile").GetBoolean());
    }

    // ---- helpers ----

    private static KimiCodeAcpAgentProvider CreateProvider(out TestWorkspace workspace)
    {
        workspace = TestWorkspace.Create("KimiProvider");
        var runtime = new KimiCodeAcpRuntime(
            new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        return new KimiCodeAcpAgentProvider(runtime);
    }
}
