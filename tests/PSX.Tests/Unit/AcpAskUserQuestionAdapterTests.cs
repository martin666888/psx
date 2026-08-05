using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpAskUserQuestionAdapterTests
{
    [TestMethod]
    public void TryCreateForm_UsesOriginalIndexesWhenSkippingMalformedQuestions()
    {
        using var document = JsonDocument.Parse("""
            {
              "title": "Ask user 2 questions",
              "rawInput": {
                "questions": [
                  {
                    "question": "Broken — missing options",
                    "header": "Bad",
                    "multiSelect": false
                  },
                  {
                    "question": "Which style?",
                    "header": "Style",
                    "multiSelect": false,
                    "options": [
                      { "label": "Scenic", "description": "Landscapes" },
                      { "label": "Cute", "description": "Pets" }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.IsTrue(AcpAskUserQuestionAdapter.TryCreateForm(
            document.RootElement,
            out var message,
            out var schema,
            out var answerIndexes));

        Assert.AreEqual("Please answer the following question(s):", message);
        CollectionAssert.AreEqual(new[] { 1 }, answerIndexes.ToArray());

        var schemaJson = JsonSerializer.Serialize(schema);
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        var properties = schemaDoc.RootElement.GetProperty("properties");
        Assert.IsFalse(properties.TryGetProperty("q0", out _));
        Assert.IsTrue(properties.TryGetProperty("q1", out _));
        Assert.IsTrue(properties.TryGetProperty("q1_other", out _));
        Assert.AreEqual(0, schemaDoc.RootElement.GetProperty("required").GetArrayLength());
    }

    [TestMethod]
    public void MapContentToAnswers_PrefersOtherAndJoinsMultiSelect()
    {
        using var content = JsonDocument.Parse("""
            {
              "q0": "Scenic",
              "q0_other": "  Custom pick  ",
              "q2": ["Alpha", "Beta"]
            }
            """);

        var answers = AcpAskUserQuestionAdapter.MapContentToAnswers(
            content.RootElement,
            new[] { 0, 2 });

        Assert.AreEqual(2, answers.Count);
        Assert.AreEqual("Custom pick", answers["0"]);
        Assert.AreEqual("Alpha, Beta", answers["2"]);
        Assert.IsFalse(answers.ContainsKey("1"));
    }

    [TestMethod]
    public void ResolveProceedOptionId_PrefersProceedOnceOfferedByAgent()
    {
        var options = new[]
        {
            new AgentDecisionOption { OptionId = "cancel", Name = "Cancel", Kind = "reject_once" },
            new AgentDecisionOption { OptionId = "proceed_once", Name = "Submit", Kind = "allow_once" }
        };

        Assert.AreEqual("proceed_once", AcpAskUserQuestionAdapter.ResolveProceedOptionId(options));
    }

    [TestMethod]
    public void TryParsePermissionResponseValue_AcceptsBareOptionIdAndFormJson()
    {
        Assert.IsTrue(AcpAskUserQuestionAdapter.TryParsePermissionResponseValue(
            "proceed_once",
            out var bareId,
            out _,
            out var hasContent));
        Assert.AreEqual("proceed_once", bareId);
        Assert.IsFalse(hasContent);

        Assert.IsTrue(AcpAskUserQuestionAdapter.TryParsePermissionResponseValue(
            """{"optionId":"proceed_once","content":{"q1":"Scenic"}}""",
            out var formId,
            out var content,
            out var hasFormContent));
        Assert.AreEqual("proceed_once", formId);
        Assert.IsTrue(hasFormContent);
        Assert.AreEqual("Scenic", content.GetProperty("q1").GetString());
    }

    [TestMethod]
    public void TryCreateForm_AcceptsStringifiedRawInput()
    {
        using var document = JsonDocument.Parse("""
            {
              "title": "Ask user 1 question",
              "rawInput": "{\"questions\":[{\"question\":\"Pick one?\",\"header\":\"Pick\",\"multiSelect\":false,\"options\":[{\"label\":\"A\",\"description\":\"a\"},{\"label\":\"B\",\"description\":\"b\"}]}]}"
            }
            """);

        Assert.IsTrue(AcpAskUserQuestionAdapter.TryCreateForm(
            document.RootElement,
            out _,
            out _,
            out var answerIndexes));
        CollectionAssert.AreEqual(new[] { 0 }, answerIndexes.ToArray());
    }
}
