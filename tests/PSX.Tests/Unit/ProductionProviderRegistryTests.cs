using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ProductionProviderRegistryTests
{
    [TestMethod]
    public void Registry_ContainsFiveCuratedProviders_DefaultStaysClaude()
    {
        using var fixture = new FakeNpmFixture(nameof(Registry_ContainsFiveCuratedProviders_DefaultStaysClaude));
        fixture.InstallKimiBundle();
        fixture.InstallQwenBundle();
        fixture.InstallOpencodeBundle();

        var claude = new ClaudeAcpAgentProvider(fixture.Manager);
        var kimi = new KimiCodeAcpAgentProvider(fixture.CreateKimiRuntime());
        var qwen = new QwenCodeAcpAgentProvider(fixture.CreateQwenRuntime());
        var opencode = new OpencodeAcpAgentProvider(fixture.CreateOpencodeRuntime());

        using var piRuntime = new PiAcpRuntime(fixture.Locator, Path.Combine(fixture.InstallDirectory, "pi-logs"));
        var pi = new PiAcpAgentProvider(piRuntime);
        var registry = new AgentProviderRegistry(
            [claude, kimi, qwen, opencode, pi],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        Assert.HasCount(5, registry.Providers);
        Assert.AreSame(pi, registry.Find("acp-pi"));
        Assert.AreSame(claude, registry.DefaultProvider);
        Assert.AreSame(claude, registry.Find("acp-claude"));
        Assert.AreSame(kimi, registry.Find("acp-kimi"));
        Assert.AreSame(qwen, registry.Find("acp-qwen"));
        Assert.AreSame(opencode, registry.Find("acp-opencode"));
        Assert.IsNull(registry.Find("acp-qoder"));
        Assert.IsNull(registry.Find("acp-cline"));

        var keys = registry.Providers.Select(provider => provider.Descriptor.Key).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "acp-claude", "acp-kimi", "acp-qwen", "acp-opencode", "acp-pi" },
            keys);
    }
}
