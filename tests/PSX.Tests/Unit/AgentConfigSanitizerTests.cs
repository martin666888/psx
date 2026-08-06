using System.IO;
using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentConfigSanitizerTests
{
    private const string Canary = "CANARY_SECRET_TOKEN_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    [TestMethod]
    public void SanitizeUrl_StripsQueryAndUserInfo()
    {
        var sanitized = AgentConfigSanitizer.SanitizeUrl(
            $"https://user:pass@example.com/mcp/path?token={Canary}#frag");

        Assert.AreEqual("https://example.com/mcp/path", sanitized);
        Assert.DoesNotContain(Canary, sanitized);
        Assert.DoesNotContain("user", sanitized);
    }

    [TestMethod]
    public void SanitizeStdioTarget_MasksLongAlphanumericArgs()
    {
        var target = AgentConfigSanitizer.SanitizeStdioTarget(
            "npx",
            ["-y", "pkg", Canary]);

        Assert.Contains("npx", target);
        Assert.Contains("•••", target);
        Assert.DoesNotContain(Canary, target);
    }

    [TestMethod]
    public void CollectObjectKeys_NeverIncludesValues()
    {
        using var doc = JsonDocument.Parse(
            $$"""{"API_KEY":"{{Canary}}","OTHER":"x"}""");
        var keys = AgentConfigSanitizer.CollectObjectKeys(doc.RootElement);

        CollectionAssert.AreEquivalent(new[] { "API_KEY", "OTHER" }, keys.ToArray());
        Assert.DoesNotContain(Canary, string.Join(',', keys));
    }

    [TestMethod]
    public void TryReadBoundedText_RejectsOversizedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psx-config-{Guid.NewGuid():N}.bin");
        try
        {
            using (var stream = File.Create(path))
            {
                stream.SetLength(AgentConfigSanitizer.MaxFileBytes + 1);
            }

            var ok = AgentConfigSanitizer.TryReadBoundedText(path, out var text, out var oversized);
            Assert.IsFalse(ok);
            Assert.IsTrue(oversized);
            Assert.AreEqual(string.Empty, text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void LooksLikeSecretAssignment_DetectsCommonKeys()
    {
        Assert.IsTrue(AgentConfigSanitizer.LooksLikeSecretAssignment($"api_key = \"{Canary}\""));
        Assert.IsFalse(AgentConfigSanitizer.LooksLikeSecretAssignment("default_model = \"kimi\""));
    }
}
