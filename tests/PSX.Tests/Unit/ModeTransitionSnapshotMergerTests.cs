using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DocumentDecisionSnapshotMergerTests
{
    [TestMethod]
    [DataRow("pending", "interrupted")]
    [DataRow("sending", "interrupted")]
    [DataRow("selected", "selected")]
    [DataRow("cancelled", "cancelled")]
    [DataRow("interrupted", "interrupted")]
    public void CloneMessage_NormalizesOnlyUnfinishedStates(string sourceState, string expectedState)
    {
        var source = CreateTransition("request-1", "tool-1", sourceState);

        var clone = DocumentDecisionSnapshotMerger.CloneMessage(source);

        Assert.AreEqual(expectedState, clone.DecisionState);
        Assert.AreEqual("mode_transition", clone.Role);
        Assert.AreNotSame(source, clone);
        Assert.AreNotSame(source.DecisionOptions, clone.DecisionOptions);
        Assert.AreNotSame(source.DecisionOptions![0], clone.DecisionOptions![0]);
    }

    [TestMethod]
    public void Merge_PromotesToolCardAndRemovesDuplicateToolOutput()
    {
        var replay = new List<AgentMessage>
        {
            new() { Role = "assistant", Text = "Before" },
            new() { Role = "tool", ToolCallId = "tool-1", RunId = "replayed-run", Text = "first" },
            new() { Role = "tool", ToolCallId = "tool-1", RunId = "replayed-run", Text = "duplicate" }
        };
        var snapshot = CreateTransition("request-1", "tool-1", "selected");

        DocumentDecisionSnapshotMerger.Merge(replay, [snapshot]);

        Assert.HasCount(2, replay);
        var transition = replay.Single(message => message.Role == "mode_transition");
        Assert.AreEqual("replayed-run", transition.RunId);
        Assert.AreEqual("selected", transition.DecisionState);
        Assert.IsFalse(replay.Any(message => message.Role == "tool" && message.ToolCallId == "tool-1"));
    }

    [TestMethod]
    public void Merge_AppendsSnapshotWhenReplayHasNoMatchingTool()
    {
        var replay = new List<AgentMessage> { new() { Role = "assistant", Text = "Before" } };

        DocumentDecisionSnapshotMerger.Merge(replay, [CreateTransition("request-1", "tool-1", "pending")]);

        Assert.HasCount(2, replay);
        Assert.AreEqual("interrupted", replay[1].DecisionState);
    }

    [TestMethod]
    public void Merge_DeduplicatesSnapshotsWithTheSameToolCallId()
    {
        var replay = new List<AgentMessage>();
        var pending = CreateTransition("request-1", "tool-1", "pending");
        var selected = CreateTransition("request-1", "tool-1", "selected");

        DocumentDecisionSnapshotMerger.Merge(replay, [pending, selected]);

        Assert.HasCount(1, replay);
        Assert.AreEqual("selected", replay[0].DecisionState);
        Assert.AreEqual("tool-1", replay[0].ToolCallId);
    }

    [TestMethod]
    public void Merge_ReusedToolCallId_PreservesDistinctHistoricalDecisions()
    {
        var replay = new List<AgentMessage>();
        var historical = CreateTransition("request-old", "tool-reused", "selected");
        historical.DecisionSnapshotId = "snapshot-old";
        historical.RunId = "run-old";
        var current = CreateTransition("request-new", "tool-reused", "selected");
        current.DecisionSnapshotId = "snapshot-new";
        current.RunId = "run-new";

        DocumentDecisionSnapshotMerger.Merge(replay, [historical, current]);

        Assert.HasCount(2, replay);
        CollectionAssert.AreEquivalent(
            new[] { "snapshot-old", "snapshot-new" },
            replay.Select(message => message.DecisionSnapshotId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "run-old", "run-new" },
            replay.Select(message => message.RunId).ToArray());
    }

    [TestMethod]
    public void Merge_PreservesDocumentPermissionRoleAndReplacesItsToolCard()
    {
        var replay = new List<AgentMessage>
        {
            new() { Role = "tool", ToolCallId = "kimi-plan", RunId = "replayed-run", Text = "raw tool" }
        };
        var document = CreateTransition("kimi-request", "kimi-plan", "selected");
        document.Role = "document_permission";

        DocumentDecisionSnapshotMerger.Merge(replay, [document]);

        Assert.HasCount(1, replay);
        Assert.AreEqual("document_permission", replay[0].Role);
        Assert.AreEqual("replayed-run", replay[0].RunId);
        Assert.AreEqual("selected", replay[0].DecisionState);
    }

    [TestMethod]
    public void InterruptPending_ChangesPendingAndSendingButPreservesTerminalStates()
    {
        var thread = new AgentThread
        {
            Messages =
            [
                CreateTransition("pending", "tool-1", "pending"),
                CreateTransition("sending", "tool-2", "sending"),
                CreateTransition("selected", "tool-3", "selected"),
                new() { Role = "document_permission", DecisionState = "pending" },
                new() { Role = "tool", DecisionState = "pending" }
            ]
        };

        var changed = DocumentDecisionSnapshotMerger.InterruptPending(thread);

        Assert.IsTrue(changed);
        Assert.AreEqual("interrupted", thread.Messages[0].DecisionState);
        Assert.AreEqual("interrupted", thread.Messages[1].DecisionState);
        Assert.AreEqual("selected", thread.Messages[2].DecisionState);
        Assert.AreEqual("interrupted", thread.Messages[3].DecisionState);
        Assert.AreEqual("pending", thread.Messages[4].DecisionState);
        Assert.IsFalse(DocumentDecisionSnapshotMerger.InterruptPending(thread));
    }

    private static AgentMessage CreateTransition(string requestId, string toolCallId, string state)
    {
        return new AgentMessage
        {
            Role = "mode_transition",
            Name = "Ready to code?",
            Text = "# Plan",
            RunId = "snapshot-run",
            ToolCallId = toolCallId,
            RequestId = requestId,
            DecisionState = state,
            SelectedOptionId = state == "selected" ? "approve" : null,
            DecisionOptions =
            [
                new AgentDecisionOption { OptionId = "approve", Name = "Approve", Kind = "allow_once" },
                new AgentDecisionOption { OptionId = "reject", Name = "Reject", Kind = "reject_once" }
            ],
            CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
        };
    }
}
