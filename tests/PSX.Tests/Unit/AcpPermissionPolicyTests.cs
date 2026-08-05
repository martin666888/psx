using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpPermissionPolicyTests
{
    [TestMethod]
    public void ReadDocument_CombinesStructuredTextBlocksInOriginalOrder()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "switch_mode",
              "content": [
                { "type": "content", "content": { "type": "text", "text": "  # Plan  " } },
                { "type": "diff", "path": "ignored.cs" },
                { "type": "text", "text": "  Second section  " },
                { "type": "content", "content": { "type": "image", "data": "ignored" } }
              ]
            }
            """);

        var result = AcpPermissionPolicy.ReadDocument(document.RootElement);

        Assert.AreEqual($"# Plan{Environment.NewLine}{Environment.NewLine}Second section", result);
    }

    [TestMethod]
    public void Classify_SwitchModeWithSingleLineText_IsModeTransition()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "switch_mode",
              "content": [ { "type": "text", "text": "Ready to code?" } ]
            }
            """);

        var result = AcpPermissionPolicy.Classify(document.RootElement);

        Assert.AreEqual(AcpPermissionPresentation.ModeTransition, result.Presentation);
        Assert.AreEqual("Ready to code?", result.DocumentText);
        Assert.AreEqual("", result.Description);
    }

    [TestMethod]
    public void Classify_SingleLineTextWithoutDiff_IsOrdinaryDescription()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "execute",
              "content": [ { "type": "text", "text": "# Markdown command details" } ]
            }
            """);

        var result = AcpPermissionPolicy.Classify(document.RootElement);

        Assert.AreEqual(AcpPermissionPresentation.Ordinary, result.Presentation);
        Assert.AreEqual("# Markdown command details", result.Description);
        Assert.AreEqual("", result.DocumentText);
        Assert.IsNull(result.ExplicitRawInput);
    }

    [TestMethod]
    public void Classify_MultilineTextOrDiff_IsDocument()
    {
        using var multiline = JsonDocument.Parse("""
            {
              "kind": "execute",
              "content": [ { "type": "text", "text": "# Plan\n\nDo the work" } ]
            }
            """);
        using var withDiff = JsonDocument.Parse("""
            {
              "kind": "edit",
              "content": [
                { "type": "diff", "path": "a.cs", "oldText": "a", "newText": "b" },
                { "type": "text", "text": "short" }
              ]
            }
            """);

        Assert.AreEqual(
            AcpPermissionPresentation.Document,
            AcpPermissionPolicy.Classify(multiline.RootElement).Presentation);
        Assert.AreEqual(
            AcpPermissionPresentation.Document,
            AcpPermissionPolicy.Classify(withDiff.RootElement).Presentation);
        Assert.AreEqual(
            "# Plan\n\nDo the work",
            AcpPermissionPolicy.Classify(multiline.RootElement).DocumentText.Replace("\r\n", "\n"));
        var withDiffText = AcpPermissionPolicy.Classify(withDiff.RootElement).DocumentText.Replace("\r\n", "\n");
        StringAssert.Contains(withDiffText, "### a.cs");
        StringAssert.Contains(withDiffText, "-a");
        StringAssert.Contains(withDiffText, "+b");
        StringAssert.Contains(withDiffText, "short");
    }

    [TestMethod]
    public void Classify_DiffOnly_IsDocumentWithNonEmptyReadableBody()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "edit",
              "content": [
                { "type": "diff", "path": "src/App.cs", "oldText": "old line", "newText": "new line" }
              ]
            }
            """);

        var result = AcpPermissionPolicy.Classify(document.RootElement);
        var body = result.DocumentText.Replace("\r\n", "\n");

        Assert.AreEqual(AcpPermissionPresentation.Document, result.Presentation);
        Assert.AreEqual("", result.Description);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.DocumentText));
        StringAssert.Contains(body, "### src/App.cs");
        StringAssert.Contains(body, "```diff");
        StringAssert.Contains(body, "-old line");
        StringAssert.Contains(body, "+new line");
    }

    [TestMethod]
    public void Classify_DoesNotStripTrailingDocumentLines()
    {
        using var document = JsonDocument.Parse("""
            {
              "content": [
                { "type": "text", "text": "# Body\n\nDetails" },
                { "type": "text", "text": "Please approve." }
              ]
            }
            """);

        var result = AcpPermissionPolicy.Classify(document.RootElement);

        Assert.AreEqual(AcpPermissionPresentation.Document, result.Presentation);
        StringAssert.Contains(result.DocumentText, "Please approve.");
        Assert.AreEqual("", result.Description);
    }

    [TestMethod]
    public void Classify_ExplicitRawInputOnly_NoToolCallJsonFallback()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "execute",
              "title": "Bash",
              "rawInput": { "command": "ls" },
              "content": [ { "type": "text", "text": "Run ls?" } ]
            }
            """);

        var result = AcpPermissionPolicy.Classify(document.RootElement);

        Assert.AreEqual(AcpPermissionPresentation.Ordinary, result.Presentation);
        Assert.AreEqual("Run ls?", result.Description);
        StringAssert.Contains(result.ExplicitRawInput, "command");
        StringAssert.Contains(result.ExplicitRawInput, "ls");
        Assert.IsFalse(result.ExplicitRawInput!.Contains("\"kind\"", StringComparison.Ordinal));
        Assert.IsFalse(result.ExplicitRawInput.Contains("\"title\"", StringComparison.Ordinal));
    }


    [TestMethod]
    public void ReadDocument_DoesNotInferDocumentFromRawInput()
    {
        using var document = JsonDocument.Parse("""
            {
              "kind": "switch_mode",
              "rawInput": { "plan": "# Hidden plan" },
              "content": []
            }
            """);

        Assert.AreEqual("", AcpPermissionPolicy.ReadDocument(document.RootElement));
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void ReadOptions_PreservesDynamicCountOrderAndValues(int count)
    {
        var source = Enumerable.Range(0, count)
            .Select(index => new
            {
                optionId = $"option-{index}",
                name = $"Option {index}",
                kind = index == count - 1 ? "reject_once" : "allow_once"
            })
            .ToArray();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { options = source }));

        var result = AcpPermissionPolicy.ReadOptions(document.RootElement);

        Assert.HasCount(count, result);
        CollectionAssert.AreEqual(source.Select(option => option.optionId).ToArray(), result.Select(option => option.OptionId).ToArray());
        CollectionAssert.AreEqual(source.Select(option => option.name).ToArray(), result.Select(option => option.Name).ToArray());
        CollectionAssert.AreEqual(source.Select(option => option.kind).ToArray(), result.Select(option => option.Kind).ToArray());
    }

    [TestMethod]
    public void ReadOptions_FiltersMissingIdsWithoutInventingDefaults()
    {
        using var document = JsonDocument.Parse("""
            {
              "options": [
                { "optionId": "", "name": "Missing" },
                { "name": "Also missing" },
                { "optionId": "approve", "name": "Approve", "kind": "allow_once" }
              ]
            }
            """);

        var result = AcpPermissionPolicy.ReadOptions(document.RootElement);

        Assert.HasCount(1, result);
        Assert.AreEqual("approve", result[0].OptionId);
    }

    [TestMethod]
    public void FindOfferedOption_ReturnsCanonicalOptionAndRejectsUnknownId()
    {
        AgentDecisionOption[] options =
        [
            new() { OptionId = "Approve_Once", Name = "Approve once", Kind = "allow_once" },
            new() { OptionId = "reject", Name = "Reject", Kind = "reject_once" }
        ];

        var selected = AcpPermissionPolicy.FindOfferedOption(options, "approve_once");

        Assert.IsNotNull(selected);
        Assert.AreSame(options[0], selected);
        Assert.AreEqual("Approve_Once", selected.OptionId);
        Assert.IsNull(AcpPermissionPolicy.FindOfferedOption(options, "not-offered"));
        Assert.IsNull(AcpPermissionPolicy.FindOfferedOption(Array.Empty<AgentDecisionOption>(), "approve_once"));
    }
}
