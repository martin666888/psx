using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ProductionProviderRegistryTests
{
    [TestMethod]
    public void Registry_ContainsFourCuratedProviders_DefaultStaysClaude()
    {
        using var fixture = new FakeNpmFixture(nameof(Registry_ContainsFourCuratedProviders_DefaultStaysClaude));
        fixture.InstallKimiBundle();
        fixture.InstallQwenBundle();
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, QoderCliAcpRuntime.SeededPackageVersion);

        var claude = new ClaudeAcpAgentProvider(fixture.Manager);
        var kimi = new KimiCodeAcpAgentProvider(fixture.CreateKimiRuntime());
        var qwen = new QwenCodeAcpAgentProvider(fixture.CreateQwenRuntime());
        var qoder = new QoderCliAcpAgentProvider(fixture.CreateQoderRuntime());

        var registry = new AgentProviderRegistry(
            [claude, kimi, qwen, qoder],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        Assert.HasCount(4, registry.Providers);
        Assert.AreSame(claude, registry.DefaultProvider);
        Assert.AreSame(claude, registry.Find("acp-claude"));
        Assert.AreSame(kimi, registry.Find("acp-kimi"));
        Assert.AreSame(qwen, registry.Find("acp-qwen"));
        Assert.AreSame(qoder, registry.Find("acp-qoder"));

        var keys = registry.Providers.Select(provider => provider.Descriptor.Key).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "acp-claude", "acp-kimi", "acp-qwen", "acp-qoder" },
            keys);
    }
}
