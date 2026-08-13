using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ClineConfigSourceTests
{
    [TestMethod]
    public void Collect_ProjectsOnlyProviderNamesSanitizedMcpAndUserSkills()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ProjectsOnlyProviderNamesSanitizedMcpAndUserSkills));
        var home = workspace.Path;
        Write(Path.Combine(home, "data", "settings", "providers.json"),
            """{"providers":{"openai":{"apiKey":"secret"},"anthropic":{"token":"secret"}}}""");
        Write(Path.Combine(home, "data", "settings", "cline_mcp_settings.json"),
            """{"mcpServers":{"remote":{"url":"https://user:pass@example.com/mcp?q=secret","headers":{"Authorization":"secret"}},"local":{"command":"npx","args":["server","ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"],"env":{"API_KEY":"secret"}}}}""");
        Directory.CreateDirectory(Path.Combine(home, "skills", "review"));
        Write(Path.Combine(home, "rules", "rules.md"), "rule");

        var report = new ClineConfigSource(() => home).Collect(CancellationToken.None);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.Facts.Any(fact => fact.Value.Contains("openai") && fact.Value.Contains("anthropic")));
        Assert.IsFalse(JsonSerializer.Serialize(report).Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(report).Contains("user:pass", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(report).Contains("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", StringComparison.Ordinal));
        Assert.IsTrue(report.McpServers.Any(server => server.Target == "https://example.com/mcp"));
        Assert.IsTrue(report.Skills.Any(skill => skill.Name == "review"));
    }

    [TestMethod]
    public void Collect_IgnoresProjectLevelConfigAndSecretFiles()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_IgnoresProjectLevelConfigAndSecretFiles));
        const string canary = "CANARY_CLINE_SECRET_abcdefghijklmnopqrstuvwxyz012345";
        var home = Path.Combine(workspace.Path, ".cline");
        Write(Path.Combine(home, "data", "settings", "providers.json"),
            """{"providers":{"openai":{"apiKey":"kept-out"}}}""");
        Write(Path.Combine(home, "data", "secrets.json"), "{\"apiKey\":\"" + canary + "\"}");
        Write(
            Path.Combine(workspace.Path, "repo", ".cline", "data", "settings", "providers.json"),
            "{\"providers\":{\"project-only\":{\"apiKey\":\"" + canary + "\"}}}");
        Write(
            Path.Combine(workspace.Path, "repo", ".cline", "data", "settings", "cline_mcp_settings.json"),
            "{\"mcpServers\":{\"leaked\":{\"url\":\"https://example.com/" + canary + "\"}}}");

        var report = new ClineConfigSource(() => home).Collect(CancellationToken.None);
        var json = JsonSerializer.Serialize(report);

        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.IsTrue(report.Facts.Any(fact => fact.Value.Contains("openai")));
        Assert.IsFalse(json.Contains("project-only", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("leaked", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(canary, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("kept-out", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Collect_OversizedKnownFile_IsPartialWithoutReadingIt()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OversizedKnownFile_IsPartialWithoutReadingIt));
        var path = Path.Combine(workspace.Path, "data", "settings", "providers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            stream.SetLength(AgentConfigSanitizer.MaxFileBytes + 1);

        var report = new ClineConfigSource(() => workspace.Path).Collect(CancellationToken.None);

        Assert.AreEqual(AgentProviderConfigReport.Partial, report.State);
        CollectionAssert.Contains(report.Notes.ToArray(), AgentConfigNotes.FileTooLarge);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
