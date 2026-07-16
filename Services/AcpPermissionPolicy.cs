using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

internal static class AcpPermissionPolicy
{
    public static AgentDecisionOption[] ReadOptions(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentDecisionOption>();
        }

        return options.EnumerateArray()
            .Select(option => new AgentDecisionOption
            {
                OptionId = GetString(option, "optionId"),
                Name = GetString(option, "name"),
                Kind = GetString(option, "kind")
            })
            .Where(option => !string.IsNullOrWhiteSpace(option.OptionId))
            .ToArray();
    }

    public static string ReadDocument(JsonElement toolCall)
    {
        if (toolCall.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !toolCall.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            var itemType = GetString(item, "type");
            if (itemType == "content"
                && item.TryGetProperty("content", out var block)
                && GetString(block, "type") == "text")
            {
                AddText(parts, GetString(block, "text"));
            }
            else if (itemType == "text")
            {
                AddText(parts, GetString(item, "text"));
            }
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    public static bool IsModeTransition(string? toolKind, string documentText)
    {
        return string.Equals(toolKind, "switch_mode", StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(documentText);
    }

    public static AgentDecisionOption? FindOfferedOption(
        IReadOnlyList<AgentDecisionOption> options,
        string? selectedOptionId)
    {
        if (string.IsNullOrWhiteSpace(selectedOptionId))
            return null;

        return options.FirstOrDefault(option =>
            string.Equals(option.OptionId, selectedOptionId, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddText(List<string> parts, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(text.Trim());
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }
}
