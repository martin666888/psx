using System.Text.Json;
using PSX.Models;
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

    [TestMethod]
    public void ClineCompatibility_DisablesUnverifiedImagesAndKeepsOnlyAct()
    {
        IAcpProviderCompatibility compatibility = new ClineAcpProviderCompatibility();
        var modes = new[]
        {
            new AcpSessionModeDescriptor("plan", "Plan", "Plan changes"),
            new AcpSessionModeDescriptor("act", "Act", "Apply changes")
        };

        Assert.IsFalse(compatibility.SupportsPromptImage(declaredSupport: true));
        var filtered = compatibility.FilterSessionModes(modes);
        Assert.HasCount(1, filtered);
        Assert.AreEqual("act", filtered[0].Id);
        Assert.IsFalse(compatibility.SupportsSessionConfigOption("mode"));
        Assert.IsTrue(compatibility.SupportsSessionConfigOption("model"));
    }
}
