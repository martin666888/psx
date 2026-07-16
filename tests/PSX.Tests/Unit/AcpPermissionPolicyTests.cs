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
    public void IsModeTransition_RequiresSwitchModeAndNonEmptyDocument()
    {
        Assert.IsTrue(AcpPermissionPolicy.IsModeTransition("switch_mode", "# Plan"));
        Assert.IsFalse(AcpPermissionPolicy.IsModeTransition("switch_mode", "  "));
        Assert.IsFalse(AcpPermissionPolicy.IsModeTransition("edit", "# Plan"));
        Assert.IsFalse(AcpPermissionPolicy.IsModeTransition(null, "# Plan"));
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
