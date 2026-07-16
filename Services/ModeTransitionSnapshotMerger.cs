using PSX.Models;

namespace PSX.Services;

internal static class ModeTransitionSnapshotMerger
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

    public static AgentMessage CloneMessage(AgentMessage message)
    {
        return new AgentMessage
        {
            Role = "mode_transition",
            Name = message.Name,
            Text = message.Text,
            RunId = message.RunId,
            ToolCallId = message.ToolCallId,
            RequestId = message.RequestId,
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
            var index = !string.IsNullOrWhiteSpace(message.ToolCallId)
                ? replayMessages.FindLastIndex(candidate =>
                    candidate.Role is "tool" or "mode_transition"
                    && string.Equals(candidate.ToolCallId, message.ToolCallId, StringComparison.Ordinal))
                : -1;

            if (index >= 0)
            {
                message.RunId = replayMessages[index].RunId ?? message.RunId;
                replayMessages[index] = message;
            }
            else
            {
                replayMessages.Add(message);
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                replayMessages.RemoveAll(candidate =>
                    !ReferenceEquals(candidate, message)
                    && candidate.Role is "tool" or "mode_transition"
                    && string.Equals(candidate.ToolCallId, message.ToolCallId, StringComparison.Ordinal));
            }
        }
    }

    public static bool InterruptPending(AgentThread thread)
    {
        var changed = false;
        foreach (var message in thread.Messages.Where(message => message.Role == "mode_transition"))
        {
            if (message.DecisionState is not ("pending" or "sending"))
                continue;

            message.DecisionState = "interrupted";
            changed = true;
        }

        return changed;
    }
}
