using System.IO;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiConfigSourceTests
{
    private const string Canary = "CANARY_KIMI_SECRET_abcdefghijklmnopqrstuvwxyz0123456789";

    [TestMethod]
    public void Collect_ScansTomlAndMcp_WithoutSecretLines()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ScansTomlAndMcp_WithoutSecretLines));
        var home = Path.Combine(workspace.Path, ".kimi-code");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), $$"""
            default_model = "kimi-k2"
            default_plan_mode = "true"
            api_key = "{{Canary}}"

            [providers.custom]
            base_url = "https://api.example.com"
            """);
        File.WriteAllText(Path.Combine(home, "mcp.json"), $$"""
            {
              "stdio": {
                "command": "node",
                "args": ["server.js", "{{Canary}}"],
                "env": { "KEY": "{{Canary}}" }
              }
            }
            """);

        var report = new KimiConfigSource(() => home).Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.Facts.Any(f => f.Label == "默认模型" && f.Value == "kimi-k2"));
        Assert.IsTrue(report.Facts.Any(f => f.Label == "自定义 Provider" && f.Value.Contains("custom")));
        Assert.DoesNotContain(Canary, json);
        Assert.IsFalse(report.Facts.Any(f => f.Value.Contains(Canary)));
    }

    [TestMethod]
    public void Collect_NestedMcpServersWrapper_ListsServers()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NestedMcpServersWrapper_ListsServers));
        var home = Path.Combine(workspace.Path, ".kimi-code");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), "default_model = \"kimi-k2\"");
        File.WriteAllText(Path.Combine(home, "mcp.json"), $$"""
            {
              "mcpServers": {
                "docs": {
                  "command": "npx",
                  "args": ["docs-server", "{{Canary}}"]
                }
              }
            }
            """);

        var report = new KimiConfigSource(() => home).Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.McpServers.Any(s => s.Name == "docs"));
        Assert.DoesNotContain(Canary, json);
    }

    [TestMethod]
    public void Collect_SecondaryModelSection_NeverMisreportsAsGlobalDefault()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SecondaryModelSection_NeverMisreportsAsGlobalDefault));
        var home = Path.Combine(workspace.Path, ".kimi-code");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), """
            default_model = "kimi-for-coding/k3"

            [secondary_model]
            default_model = "kimi-code/kimi-for-coding-highspeed"
            force = true
            """);

        var report = new KimiConfigSource(() => home).Collect(CancellationToken.None);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        var defaultFacts = report.Facts.Where(f => f.Label == "默认模型").ToArray();
        Assert.HasCount(1, defaultFacts);
        Assert.AreEqual("kimi-for-coding/k3", defaultFacts[0].Value);
        Assert.IsTrue(report.Facts.Any(f =>
            f.Label == "子 Agent 模型" && f.Value == "kimi-code/kimi-for-coding-highspeed"));
    }

    [TestMethod]
    public void Collect_SecondaryModelPool_ListsQuotedAliases()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SecondaryModelPool_ListsQuotedAliases));
        var home = Path.Combine(workspace.Path, ".kimi-code");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), """
            default_model = "kimi-for-coding/k3"

            [secondary_model]
            default_model = "kimi-for-coding/k3"

            [secondary_model.models]
            "kimi-code/kimi-for-coding-highspeed" = "Fast and cheap."
            "kimi-code/k3" = "Strong at complex reasoning."
            """);

        var report = new KimiConfigSource(() => home).Collect(CancellationToken.None);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        var pool = report.Facts.Single(f => f.Label == "子 Agent 模型池");
        Assert.Contains("kimi-code/kimi-for-coding-highspeed", pool.Value);
        Assert.Contains("kimi-code/k3", pool.Value);
        Assert.DoesNotContain("Fast and cheap", pool.Value);
    }
}
