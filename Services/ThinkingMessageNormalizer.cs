using PSX.Models;

namespace PSX.Services;

internal static class ThinkingMessageNormalizer
{
    public static bool Normalize(List<AgentMessage> messages)
    {
        if (messages.Count == 0)
            return false;

        var normalized = new List<AgentMessage>(messages.Count);
        var changed = false;
        var turnNumber = 0;
        var index = 0;

        while (index < messages.Count)
        {
            var user = messages[index];
            if (user.Role != "user")
            {
                normalized.Add(user);
                index++;
                continue;
            }

            turnNumber++;
            var turnEnd = index + 1;
            while (turnEnd < messages.Count && messages[turnEnd].Role != "user")
                turnEnd++;

            var runId = user.RunId;
            if (string.IsNullOrWhiteSpace(runId))
            {
                runId = $"history-turn-{turnNumber}";
                user.RunId = runId;
                changed = true;
            }

            normalized.Add(user);
            var thinkingMessages = messages
                .Skip(index + 1)
                .Take(turnEnd - index - 1)
                .Where(message => message.Role == "thinking")
                .ToArray();
            var nonEmptyThinking = thinkingMessages
                .Where(message => !string.IsNullOrWhiteSpace(message.Text))
                .ToArray();

            if (nonEmptyThinking.Length > 0)
            {
                var thinking = nonEmptyThinking[0];
                var combinedText = string.Join("\n\n", nonEmptyThinking.Select(message => message.Text));
                if (!string.Equals(thinking.Text, combinedText, StringComparison.Ordinal))
                {
                    thinking.Text = combinedText;
                    changed = true;
                }

                if (!string.Equals(thinking.RunId, runId, StringComparison.Ordinal))
                {
                    thinking.RunId = runId;
                    changed = true;
                }

                normalized.Add(thinking);
            }

            if (thinkingMessages.Length != 1
                || (thinkingMessages.Length == 1 && nonEmptyThinking.Length == 0)
                || (nonEmptyThinking.Length == 1 && !ReferenceEquals(messages[index + 1], nonEmptyThinking[0])))
            {
                changed = thinkingMessages.Length > 0 || changed;
            }

            for (var turnIndex = index + 1; turnIndex < turnEnd; turnIndex++)
            {
                if (messages[turnIndex].Role != "thinking")
                    normalized.Add(messages[turnIndex]);
            }

            index = turnEnd;
        }

        if (!changed && !messages.SequenceEqual(normalized, ReferenceEqualityComparer.Instance))
            changed = true;

        if (!changed)
            return false;

        messages.Clear();
        messages.AddRange(normalized);
        return true;
    }
}
