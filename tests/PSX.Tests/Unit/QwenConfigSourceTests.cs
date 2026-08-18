using System.IO;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QwenConfigSourceTests
{
    private const string Canary = "CANARY_QWEN_SECRET_abcdefghijklmnopqrstuvwxyz0123456789";

    [TestMethod]
    public void Collect_MapsHttpUrlAndEnvKeys()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MapsHttpUrlAndEnvKeys));
        var home = Path.Combine(workspace.Path, ".qwen");
        Directory.CreateDirectory(Path.Combine(home, "skills", "qwen-skill"));
        File.WriteAllText(Path.Combine(home, "settings.json"), $$"""
            {
              "model": { "name": "qwen3-coder" },
              "modelProviders": {
                "dashscope": {
                  "name": "DashScope",
                  "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1?key={{Canary}}"
                }
              },
              "mcpServers": {
                "http": {
                  "httpUrl": "https://mcp.qwen.example/path?token={{Canary}}",
                  "headers": { "Authorization": "{{Canary}}" }
                }
              },
              "env": { "DASHSCOPE_API_KEY": "{{Canary}}" },
              "security": { "auth": { "selectedType": "oauth" } }
            }
            """);

        var report = new QwenConfigSource(() => home).Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.Facts.Any(f => f.Label == "默认模型"));
        Assert.IsTrue(report.Facts.Any(f => f.Label == "认证类型" && f.Value == "oauth"));
        Assert.IsTrue(report.Models.Any(m =>
            m.Id == "dashscope" && m.BaseUrl == "https://dashscope.aliyuncs.com/compatible-mode/v1"));
        Assert.IsTrue(report.McpServers.Any(m =>
            m.Transport == AgentConfigMcpServer.TransportHttp
            && m.Target == "https://mcp.qwen.example/path"));
        Assert.IsTrue(report.Skills.Any(s => s.Name == "qwen-skill"));
        Assert.DoesNotContain(Canary, json);
    }

    [TestMethod]
    public void Collect_StdioMcp_MasksLongTokenArgs()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_StdioMcp_MasksLongTokenArgs));
        var home = Path.Combine(workspace.Path, ".qwen");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"), $$"""
            {
              "mcpServers": {
                "local": {
                  "command": "node",
                  "args": ["server.js", "--token", "{{Canary}}"],
                  "env": { "API_KEY": "{{Canary}}" }
                }
              }
            }
            """);

        var report = new QwenConfigSource(() => home).Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        var server = report.McpServers.Single(m => m.Name == "local");
        Assert.AreEqual(AgentConfigMcpServer.TransportStdio, server.Transport);
        Assert.StartsWith("node server.js --token", server.Target);
        Assert.IsTrue(server.EnvKeys.Contains("API_KEY"));
        Assert.DoesNotContain(Canary, json);
    }
}
