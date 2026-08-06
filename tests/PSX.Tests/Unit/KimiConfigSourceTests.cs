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
}
