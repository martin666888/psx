using System.IO;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class OpencodeConfigSourceTests
{
    private const string Canary = "CANARY_OPENCODE_SECRET_abcdefghijklmnopqrstuvwxyz012345";

    [TestMethod]
    public void Collect_ParsesJsoncMcpAndAuthIdsOnly()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ParsesJsoncMcpAndAuthIdsOnly));
        var configDir = Path.Combine(workspace.Path, "config", "opencode");
        var authRoot = Path.Combine(workspace.Path, "auth");
        Directory.CreateDirectory(Path.Combine(configDir, "skills", "oc-skill"));
        Directory.CreateDirectory(authRoot);

        var configPath = Path.Combine(configDir, "opencode.jsonc");
        File.WriteAllText(configPath, $$"""
            {
              // comment
              "model": "anthropic/claude-sonnet-4",
              "provider": {
                "custom": {
                  "options": {
                    "baseURL": "https://api.example.com/v1?key={{Canary}}"
                  },
                  "models": {
                    "fast": { "name": "Fast" }
                  }
                }
              },
              "disabled_providers": ["openai"],
              "instructions": ["GLOBAL.md"],
              "mcp": {
                "local": {
                  "type": "local",
                  "command": ["npx", "-y", "{{Canary}}"],
                  "environment": { "TOKEN": "{{Canary}}" },
                  "env": { "OTHER": "{{Canary}}" }
                },
                "remote": {
                  "type": "remote",
                  "url": "https://mcp.example.com/rpc?token={{Canary}}",
                  "headers": { "Authorization": "Bearer {{Canary}}" }
                }
              },
            }
            """);
        File.WriteAllText(Path.Combine(authRoot, "auth.json"), $$"""
            {
              "anthropic": { "type": "api", "key": "{{Canary}}" },
              "openai": { "type": "oauth", "refresh": "{{Canary}}" }
            }
            """);

        var source = new OpencodeConfigSource(
            () => configPath,
            () => configDir,
            () => authRoot);
        var report = source.Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.Facts.Any(f => f.LabelKey == AgentConfigFactLabels.DefaultModel));
        Assert.IsTrue(report.Facts.Any(f => f.LabelKey == AgentConfigFactLabels.SavedCredentialProviders && f.Value.Contains("anthropic")));
        Assert.IsTrue(report.Skills.Any(s => s.Name == "oc-skill"));
        Assert.IsTrue(report.McpServers.Any(m =>
            m.Name == "remote" && m.Target == "https://mcp.example.com/rpc"));
        Assert.IsTrue(report.McpServers.Any(m =>
            m.Name == "local" && m.EnvKeys.Contains("TOKEN") && m.EnvKeys.Contains("OTHER")));
        Assert.DoesNotContain(Canary, json);
    }
}
