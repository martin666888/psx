using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ThinkingMessageNormalizerTests
{
    [TestMethod]
    public void Normalize_CombinesThinkingAtTheStartOfItsTurnWithoutReorderingOtherMessages()
    {
        var messages = new List<AgentMessage>
        {
            new() { Role = "user", Text = "Question", RunId = "run-1" },
            new() { Role = "assistant", Text = "Before" },
            new() { Role = "thinking", Text = "Inspecting", RunId = "old-run" },
            new() { Role = "tool", Text = "Tool", ToolCallId = "tool-1" },
            new() { Role = "thinking", Text = "Verifying" },
            new() { Role = "assistant", Text = "After" }
        };

        Assert.IsTrue(ThinkingMessageNormalizer.Normalize(messages));

        CollectionAssert.AreEqual(
            new[] { "user", "thinking", "assistant", "tool", "assistant" },
            messages.Select(message => message.Role).ToArray());
        Assert.AreEqual("Inspecting\n\nVerifying", messages[1].Text);
        Assert.AreEqual("run-1", messages[1].RunId);
        CollectionAssert.AreEqual(
            new[] { "Before", "Tool", "After" },
            messages.Where(message => message.Role != "user" && message.Role != "thinking")
                .Select(message => message.Text).ToArray());
    }

    [TestMethod]
    public void Normalize_SeparatesTurnsAssignsStableRunIdsAndIsIdempotent()
    {
        var messages = new List<AgentMessage>
        {
            new() { Role = "user", Text = "First" },
            new() { Role = "thinking", Text = "One" },
            new() { Role = "assistant", Text = "First answer" },
            new() { Role = "user", Text = "Second" },
            new() { Role = "thinking", Text = "Two" },
            new() { Role = "assistant", Text = "Second answer" }
        };

        Assert.IsTrue(ThinkingMessageNormalizer.Normalize(messages));
        var snapshot = messages.Select(message => (message.Role, message.Text, message.RunId)).ToArray();

        Assert.AreEqual("history-turn-1", messages[0].RunId);
        Assert.AreEqual("history-turn-1", messages[1].RunId);
        Assert.AreEqual("history-turn-2", messages[3].RunId);
        Assert.AreEqual("history-turn-2", messages[4].RunId);
        Assert.IsFalse(ThinkingMessageNormalizer.Normalize(messages));
        CollectionAssert.AreEqual(snapshot, messages.Select(message => (message.Role, message.Text, message.RunId)).ToArray());
    }

    [TestMethod]
    public void Normalize_RemovesEmptyThinkingAndPreservesOrphanMessages()
    {
        var orphan = new AgentMessage { Role = "thinking", Text = "Orphan" };
        var messages = new List<AgentMessage>
        {
            orphan,
            new() { Role = "user", Text = "Question", RunId = "run-1" },
            new() { Role = "thinking", Text = "   " },
            new() { Role = "assistant", Text = "Answer" }
        };

        Assert.IsTrue(ThinkingMessageNormalizer.Normalize(messages));

        Assert.AreSame(orphan, messages[0]);
        Assert.IsFalse(messages.Skip(1).Any(message => message.Role == "thinking"));
    }

    [TestMethod]
    public void Normalize_AfterModeTransitionMergePreservesPromotionAndSelection()
    {
        var messages = new List<AgentMessage>
        {
            new() { Role = "user", Text = "Question", RunId = "run-1" },
            new() { Role = "thinking", Text = "First", RunId = "run-1" },
            new() { Role = "assistant", Text = "Before" },
            new() { Role = "tool", Text = "Switch", ToolCallId = "tool-1", RunId = "run-1" },
            new() { Role = "thinking", Text = "Second", RunId = "run-1" }
        };
        var snapshot = new AgentMessage
        {
            Role = "mode_transition",
            Text = "# Plan",
            ToolCallId = "tool-1",
            RequestId = "request-1",
            DecisionState = "selected",
            SelectedOptionId = "approve"
        };

        DocumentDecisionSnapshotMerger.Merge(messages, [snapshot]);
        ThinkingMessageNormalizer.Normalize(messages);

        CollectionAssert.AreEqual(
            new[] { "user", "thinking", "assistant", "mode_transition" },
            messages.Select(message => message.Role).ToArray());
        var transition = messages.Single(message => message.Role == "mode_transition");
        Assert.AreEqual("selected", transition.DecisionState);
        Assert.AreEqual("approve", transition.SelectedOptionId);
        Assert.AreEqual("run-1", transition.RunId);
        Assert.IsFalse(messages.Any(message => message.Role == "tool"));
    }
}
