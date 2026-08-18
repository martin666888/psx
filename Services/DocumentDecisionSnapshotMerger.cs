using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Preserves locally-rendered document decisions across ACP history replay.
/// Mode transitions are one document-decision presentation; generic document
/// permissions are another and deliberately keep their distinct transcript role.
/// </summary>
internal static class DocumentDecisionSnapshotMerger
{
    public static AgentDecisionOption CloneOption(AgentDecisionOption option)
    {
        return new AgentDecisionOption
        {
            OptionId = option.OptionId,
            Name = option.Name,
            Kind = option.Kind
        };
    }

    public static bool IsDocumentDecision(AgentMessage message)
    {
        return message.Role is "mode_transition" or "document_permission";
    }

    public static AgentMessage CloneMessage(AgentMessage message)
    {
        return new AgentMessage
        {
            Role = message.Role,
            Name = message.Name,
            Text = message.Text,
            RunId = message.RunId,
            ToolCallId = message.ToolCallId,
            RequestId = message.RequestId,
            DecisionSnapshotId = message.DecisionSnapshotId,
            DecisionState = message.DecisionState is "pending" or "sending"
                ? "interrupted"
                : message.DecisionState,
            SelectedOptionId = message.SelectedOptionId,
            DecisionOptions = message.DecisionOptions?.Select(CloneOption).ToList(),
            CreatedAt = message.CreatedAt
        };
    }

    public static void Merge(List<AgentMessage> replayMessages, IReadOnlyList<AgentMessage> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var message = CloneMessage(snapshot);
            var index = -1;
            if (!string.IsNullOrWhiteSpace(message.DecisionSnapshotId))
            {
                index = replayMessages.FindLastIndex(candidate =>
                    IsDocumentDecision(candidate)
                    && string.Equals(
                        candidate.DecisionSnapshotId,
                        message.DecisionSnapshotId,
                        StringComparison.Ordinal));
            }

            // Backward compatibility for snapshots written before
            // DecisionSnapshotId existed: only the same logical decision may
            // replace an earlier state. A reused toolCallId by itself is never
            // enough to merge two persisted decisions.
            if (index < 0 && string.IsNullOrWhiteSpace(message.DecisionSnapshotId))
            {
                index = replayMessages.FindLastIndex(candidate =>
                    candidate.Role == message.Role
                    && string.Equals(candidate.RequestId, message.RequestId, StringComparison.Ordinal)
                    && string.Equals(candidate.ToolCallId, message.ToolCallId, StringComparison.Ordinal));
            }

            // Promote the raw replay tool card for this run into the persisted
            // decision card. Prefer the snapshot's run when the provider replay
            // preserved it; otherwise consume only the newest raw tool card and
            // never another historical decision.
            if (index < 0 && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                index = replayMessages.FindLastIndex(candidate =>
                    candidate.Role == "tool"
                    && string.Equals(candidate.ToolCallId, message.ToolCallId, StringComparison.Ordinal)
                    && string.Equals(candidate.RunId, message.RunId, StringComparison.Ordinal));
                if (index < 0)
                {
                    index = replayMessages.FindLastIndex(candidate =>
                        candidate.Role == "tool"
                        && string.Equals(candidate.ToolCallId, message.ToolCallId, StringComparison.Ordinal));
                }
            }

            string? matchedRunId = null;
            if (index >= 0)
            {
                matchedRunId = replayMessages[index].RunId;
                message.RunId = replayMessages[index].RunId ?? message.RunId;
                replayMessages[index] = message;
            }
            else
            {
                replayMessages.Add(message);
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId) && matchedRunId != null)
            {
                replayMessages.RemoveAll(candidate =>
                    !ReferenceEquals(candidate, message)
                    && candidate.Role == "tool"
                    && string.Equals(candidate.ToolCallId, message.ToolCallId, StringComparison.Ordinal)
                    && string.Equals(candidate.RunId, matchedRunId, StringComparison.Ordinal));
            }
        }
    }

    public static bool InterruptPending(AgentThread thread)
    {
        var changed = false;
        foreach (var message in thread.Messages.Where(IsDocumentDecision))
        {
            if (message.DecisionState is not ("pending" or "sending"))
                continue;

            message.DecisionState = "interrupted";
            changed = true;
        }

        return changed;
    }
}
