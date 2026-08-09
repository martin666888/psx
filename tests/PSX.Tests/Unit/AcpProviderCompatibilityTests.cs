using System.Text.Json;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpProviderCompatibilityTests
{
    [TestMethod]
    public void DefaultCompatibility_UsesStandardTitleAndHasNoLegacyAlias()
    {
        IAcpProviderCompatibility compatibility = DefaultAcpProviderCompatibility.Instance;
        using var update = JsonDocument.Parse(
            """{"title":"Standard title","_meta":{"claudeCode":{"toolName":"Claude name"}}}""");

        Assert.AreEqual("Standard title", compatibility.ResolveToolName(update.RootElement));
        Assert.IsFalse(compatibility.IsLegacyPromptCommand("claude_command"));
    }

    [TestMethod]
    public void ClaudeCompatibility_UsesClaudeMetadataAndOwnsLegacyAlias()
    {
        IAcpProviderCompatibility compatibility = new ClaudeAcpProviderCompatibility();
        using var update = JsonDocument.Parse(
            """{"title":"Standard title","_meta":{"claudeCode":{"toolName":"Read"}}}""");

        Assert.AreEqual("Read", compatibility.ResolveToolName(update.RootElement));
        Assert.IsTrue(compatibility.IsLegacyPromptCommand("claude_command"));
        Assert.IsFalse(compatibility.IsLegacyPromptCommand("agent_command"));
    }

    [TestMethod]
    public void ClaudeCompatibility_MissingExtension_FallsBackToStandardKind()
    {
        IAcpProviderCompatibility compatibility = new ClaudeAcpProviderCompatibility();
        using var update = JsonDocument.Parse("""{"kind":"execute"}""");

        Assert.AreEqual("execute", compatibility.ResolveToolName(update.RootElement));
    }
}
