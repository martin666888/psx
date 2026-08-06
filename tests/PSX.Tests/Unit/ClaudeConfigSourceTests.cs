using System.IO;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ClaudeConfigSourceTests
{
    private const string Canary = "CANARY_CLAUDE_SECRET_abcdefghijklmnopqrstuvwxyz012345";

    [TestMethod]
    public void Collect_ReadsSettingsMcpAndSkills_WithoutSecretValues()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ReadsSettingsMcpAndSkills_WithoutSecretValues));
        var home = Path.Combine(workspace.Path, ".claude");
        Directory.CreateDirectory(Path.Combine(home, "skills", "demo-skill"));
        File.WriteAllText(Path.Combine(home, "settings.json"), $$"""
            {
              "model": "claude-sonnet",
              "env": { "ANTHROPIC_API_KEY": "{{Canary}}" },
              "enabledPlugins": { "a": true, "b": true }
            }
            """);
        File.WriteAllText(Path.Combine(home, "settings.local.json"), """
            { "permissions": { "allow": ["Bash(git *)", "Read"] } }
            """);
        var claudeJson = Path.Combine(workspace.Path, ".claude.json");
        File.WriteAllText(claudeJson, $$"""
            {
              "mcpServers": {
                "remote": {
                  "url": "https://mcp.example.com/v1?token={{Canary}}",
                  "headers": { "Authorization": "Bearer {{Canary}}" }
                },
                "local": {
                  "command": "npx",
                  "args": ["-y", "{{Canary}}"],
                  "env": { "TOKEN": "{{Canary}}" }
                }
              }
            }
            """);

        var source = new ClaudeConfigSource(() => home, () => claudeJson);
        var report = source.Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.Facts.Any(f => f.Label == "默认模型" && f.Value == "claude-sonnet"));
        Assert.IsTrue(report.Facts.Any(f => f.Label == "环境变量键" && f.Value.Contains("ANTHROPIC_API_KEY")));
        Assert.IsTrue(report.Skills.Any(s => s.Name == "demo-skill"));
        Assert.HasCount(2, report.McpServers);
        Assert.DoesNotContain(Canary, json);
        Assert.IsTrue(report.McpServers.Any(m =>
            m.Name == "remote" && m.Target == "https://mcp.example.com/v1"));
        Assert.IsTrue(report.McpServers.Any(m =>
            m.Name == "local" && m.Target.Contains("•••")));
    }

    [TestMethod]
    public void Collect_OversizedSettings_IsPartialWithNote()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OversizedSettings_IsPartialWithNote));
        var home = Path.Combine(workspace.Path, ".claude");
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "settings.json");
        using (var stream = File.Create(path))
            stream.SetLength(AgentConfigSanitizer.MaxFileBytes + 10);

        var report = new ClaudeConfigSource(() => home, () => null).Collect(CancellationToken.None);
        Assert.AreEqual(AgentProviderConfigReport.Partial, report.State);
        CollectionAssert.Contains(report.Notes.ToArray(), AgentConfigNotes.FileTooLarge);
    }
}
