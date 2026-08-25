using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Structural adapter for ACP agents that ask the user via
/// <c>session/request_permission</c> with nested <c>toolCall.rawInput.questions</c>
/// (for example Qwen Code's <c>ask_user_question</c>).
/// Builds a shared elicitation-shaped schema for the permission form variant,
/// then maps form content back to <c>answers: Record&lt;string, string&gt;</c>
/// keyed by the <b>original</b> questions-array decimal indexes.
/// </summary>
internal static class AcpAskUserQuestionAdapter
{
    internal sealed record AskUserOption(string Label, string Description);

    internal sealed record AskUserQuestion(
        int OriginalIndex,
        string Question,
        string Header,
        bool MultiSelect,
        IReadOnlyList<AskUserOption> Options);

    /// <summary>
    /// When <paramref name="toolCall"/> carries a non-empty <c>rawInput.questions</c>
    /// array with at least one usable question, builds an elicitation-compatible
    /// schema. Field keys and answer indexes always use the original array index
    /// so skipped malformed entries do not shift later answers.
    /// </summary>
    public static bool TryCreateForm(
        JsonElement toolCall,
        out string message,
        out object schema,
        out IReadOnlyList<int> answerIndexes)
    {
        message = "";
        schema = new Dictionary<string, object?>();
        answerIndexes = Array.Empty<int>();

        if (!TryReadQuestions(toolCall, out var questions) || questions.Count == 0)
            return false;

        message = "";
        // The frontend supplies the localized form prompt when the Agent did
        // not provide one; no PSX-authored display sentence crosses the bridge.

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var indexes = new List<int>(questions.Count);
        foreach (var question in questions)
        {
            var key = FieldKey(question.OriginalIndex);
            var oneOf = question.Options
                .Select(option => (object)new Dictionary<string, object?>
                {
                    ["const"] = option.Label,
                    ["title"] = option.Label,
                    ["description"] = option.Description
                })
                .ToArray();

            if (question.MultiSelect)
            {
                properties[key] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["title"] = question.Header,
                    ["description"] = question.Question,
                    ["uniqueItems"] = true,
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["oneOf"] = oneOf
                    }
                };
            }
            else
            {
                properties[key] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["title"] = question.Header,
                    ["description"] = question.Question,
                    ["oneOf"] = oneOf
                };
            }

            // Do not mark q{i} required: Qwen allows free-text "Other" to
            // stand in for a preset option. MapContentToAnswers prefers Other.
            properties[OtherFieldKey(question.OriginalIndex)] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                // An untitled supplemental field is rendered through the
                // frontend's localized "Other" fallback.
                ["title"] = ""
            };
            indexes.Add(question.OriginalIndex);
        }

        schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = Array.Empty<string>()
        };
        answerIndexes = indexes;
        return true;
    }

    /// <summary>
    /// Maps elicitation form content (<c>qN</c>/<c>qN_other</c>…) to the
    /// permission-response <c>answers</c> map. Keys are canonical decimal
    /// strings of the original question indexes in <paramref name="answerIndexes"/>.
    /// </summary>
    public static Dictionary<string, string> MapContentToAnswers(
        JsonElement content,
        IReadOnlyList<int> answerIndexes)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (content.ValueKind != JsonValueKind.Object || answerIndexes.Count == 0)
            return answers;

        foreach (var index in answerIndexes)
        {
            var other = ReadStringProperty(content, OtherFieldKey(index));
            if (!string.IsNullOrWhiteSpace(other))
            {
                answers[AnswerKey(index)] = other.Trim();
                continue;
            }

            if (!content.TryGetProperty(FieldKey(index), out var value))
                continue;

            var formatted = FormatAnswerValue(value);
            if (!string.IsNullOrWhiteSpace(formatted))
                answers[AnswerKey(index)] = formatted;
        }

        return answers;
    }

    /// <summary>
    /// Prefer the agent's explicit proceed/allow option (Qwen: <c>proceed_once</c>).
    /// The chosen id must be one of the offered options.
    /// </summary>
    public static string? ResolveProceedOptionId(IReadOnlyList<AgentDecisionOption> options)
    {
        if (options.Count == 0)
            return null;

        var exact = options.FirstOrDefault(option =>
            string.Equals(option.OptionId, "proceed_once", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.OptionId, "allow_once", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.OptionId, "allow", StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact.OptionId;

        var byKind = options.FirstOrDefault(option =>
            string.Equals(option.Kind, "allow_once", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.Kind, "allow_always", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.Kind, "allow", StringComparison.OrdinalIgnoreCase));
        if (byKind != null)
            return byKind.OptionId;

        return options.FirstOrDefault(option =>
            !string.Equals(option.Kind, "reject_once", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(option.Kind, "reject_always", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(option.OptionId, "cancel", StringComparison.OrdinalIgnoreCase)
            && !option.OptionId.Contains("reject", StringComparison.OrdinalIgnoreCase)
            && !option.OptionId.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            ?.OptionId;
    }

    public static string? ResolveCancelOptionId(IReadOnlyList<AgentDecisionOption> options)
    {
        if (options.Count == 0)
            return null;

        var exact = options.FirstOrDefault(option =>
            string.Equals(option.OptionId, "cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.OptionId, "reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.OptionId, "reject_once", StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact.OptionId;

        return options.FirstOrDefault(option =>
            string.Equals(option.Kind, "reject_once", StringComparison.OrdinalIgnoreCase)
            || string.Equals(option.Kind, "reject_always", StringComparison.OrdinalIgnoreCase)
            || option.OptionId.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || option.OptionId.Contains("reject", StringComparison.OrdinalIgnoreCase))
            ?.OptionId;
    }

    /// <summary>
    /// Parses a permission-response <c>value</c>: either a bare optionId or
    /// JSON <c>{ optionId, content }</c> from the form variant.
    /// </summary>
    public static bool TryParsePermissionResponseValue(
        string? raw,
        out string optionId,
        out JsonElement content,
        out bool hasContent)
    {
        optionId = "";
        content = default;
        hasContent = false;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                optionId = GetString(root, "optionId").Trim();
                if (string.IsNullOrWhiteSpace(optionId))
                    return false;

                if (root.TryGetProperty("content", out var contentElement)
                    && contentElement.ValueKind == JsonValueKind.Object)
                {
                    content = contentElement.Clone();
                    hasContent = true;
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        optionId = trimmed;
        return true;
    }

    internal static bool TryReadQuestions(
        JsonElement toolCall,
        out IReadOnlyList<AskUserQuestion> questions)
    {
        questions = Array.Empty<AskUserQuestion>();
        if (toolCall.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;

        if (!TryGetQuestionsArray(toolCall, out var questionsElement))
            return false;

        var parsed = new List<AskUserQuestion>();
        var originalIndex = 0;
        foreach (var item in questionsElement.EnumerateArray())
        {
            var index = originalIndex;
            originalIndex++;

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var questionText = GetString(item, "question").Trim();
            var header = GetString(item, "header").Trim();
            if (string.IsNullOrWhiteSpace(questionText) || string.IsNullOrWhiteSpace(header))
                continue;

            if (!item.TryGetProperty("options", out var optionsElement)
                || optionsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var options = new List<AskUserOption>();
            foreach (var option in optionsElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object)
                    continue;

                var label = GetString(option, "label").Trim();
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                options.Add(new AskUserOption(label, GetString(option, "description").Trim()));
            }

            if (options.Count == 0)
                continue;

            var multiSelect = item.TryGetProperty("multiSelect", out var multi)
                              && multi.ValueKind == JsonValueKind.True;

            parsed.Add(new AskUserQuestion(index, questionText, header, multiSelect, options));
        }

        if (parsed.Count == 0)
            return false;

        questions = parsed;
        return true;
    }

    private static bool TryGetQuestionsArray(JsonElement toolCall, out JsonElement questions)
    {
        questions = default;
        if (!toolCall.TryGetProperty("rawInput", out var rawInput))
            return false;

        if (rawInput.ValueKind == JsonValueKind.Object
            && rawInput.TryGetProperty("questions", out var fromObject)
            && fromObject.ValueKind == JsonValueKind.Array)
        {
            questions = fromObject;
            return true;
        }

        // Some adapters stringify rawInput; accept a JSON object string.
        if (rawInput.ValueKind == JsonValueKind.String)
        {
            var raw = rawInput.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("questions", out var fromString)
                        && fromString.ValueKind == JsonValueKind.Array)
                    {
                        questions = fromString.Clone();
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Fall through to non-ask-user handling.
                }
            }
        }

        return false;
    }

    private static string FormatAnswerValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Array => string.Join(
                ", ",
                value.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String
                        ? item.GetString()?.Trim() ?? ""
                        : item.GetRawText())
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => ""
        };
    }

    private static string ReadStringProperty(JsonElement content, string name)
    {
        return content.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    internal static string FieldKey(int index) => $"q{index}";

    internal static string OtherFieldKey(int index) => $"q{index}_other";

    internal static string AnswerKey(int index) =>
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }
}
