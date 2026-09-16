using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

internal enum AcpPermissionPresentation
{
    Ordinary,
    Document,
    ModeTransition
}

internal sealed record AcpPermissionContent(
    AcpPermissionPresentation Presentation,
    string DocumentText,
    string Description,
    string? ExplicitRawInput);

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

    public static List<AgentEditBlock>? ReadEditBlocks(JsonElement toolCall, string? workingDirectory)
    {
        if (!HasDiff(toolCall))
            return null;

        var blocks = new List<AgentEditBlock>();
        foreach (var item in toolCall.GetProperty("content").EnumerateArray())
        {
            var type = GetString(item, "type");
            if (type == "diff")
            {
                // Incomplete provider payloads stay on the existing document
                // fallback, rather than displaying a fabricated empty edit.
                var path = GetString(item, "path");
                if (string.IsNullOrWhiteSpace(path)
                    || !item.TryGetProperty("newText", out var next)
                    || next.ValueKind != JsonValueKind.String)
                    return null;
                string? oldText = null;
                if (item.TryGetProperty("oldText", out var previous))
                {
                    if (previous.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                        return null;
                    oldText = previous.ValueKind == JsonValueKind.String ? previous.GetString() : null;
                }

                var displayPath = path;
                var fullPath = path;
                var external = true;
                try
                {
                    if (!string.IsNullOrWhiteSpace(workingDirectory) && Path.IsPathFullyQualified(workingDirectory))
                    {
                        fullPath = Path.GetFullPath(path, workingDirectory);
                        var relative = Path.GetRelativePath(workingDirectory, fullPath);
                        external = Path.IsPathRooted(relative) || relative == ".."
                            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                        displayPath = external ? fullPath : relative.Replace('\\', '/');
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Keep the exact provider target when it cannot be resolved.
                }
                blocks.Add(new AgentEditBlock("diff", Path: fullPath, DisplayPath: displayPath,
                    External: external, OldText: oldText, NewText: next.GetString() ?? ""));
            }
            else if (type == "text")
                blocks.Add(new AgentEditBlock("text", Text: GetString(item, "text")));
            else if (type == "content" && item.TryGetProperty("content", out var content)
                && GetString(content, "type") == "text")
                blocks.Add(new AgentEditBlock("text", Text: GetString(content, "text")));
            else
                return null;
        }
        return blocks;
    }

    /// <summary>
    /// Deterministic permission classification. Never strips trailing "approval"
    /// hints from document bodies. Diff blocks are formatted into DocumentText
    /// so pure-diff Document requests stay non-empty for the frontend gate.
    /// </summary>
    public static AcpPermissionContent Classify(JsonElement toolCall)
    {
        var toolKind = GetString(toolCall, "kind");
        var explicitRawInput = ReadExplicitRawInput(toolCall);
        var textBlocks = ReadTextBlocks(toolCall);
        var hasDiff = HasDiff(toolCall);
        var documentText = BuildDocumentText(toolCall);
        var anyBlockHasNewline = textBlocks.Any(block =>
            block.Contains('\n', StringComparison.Ordinal)
            || block.Contains('\r', StringComparison.Ordinal));

        if (string.Equals(toolKind, "switch_mode", StringComparison.Ordinal)
            && textBlocks.Count > 0)
        {
            return new AcpPermissionContent(
                AcpPermissionPresentation.ModeTransition,
                documentText,
                Description: "",
                explicitRawInput);
        }

        if (hasDiff || anyBlockHasNewline)
        {
            return new AcpPermissionContent(
                AcpPermissionPresentation.Document,
                documentText,
                Description: "",
                explicitRawInput);
        }

        var description = textBlocks.Count == 1 ? textBlocks[0] : "";
        return new AcpPermissionContent(
            AcpPermissionPresentation.Ordinary,
            DocumentText: "",
            description,
            explicitRawInput);
    }

    /// <summary>
    /// Combined readable text blocks (no diffs). Kept for tests and callers that
    /// only need the joined body after classification.
    /// </summary>
    public static string ReadDocument(JsonElement toolCall)
    {
        var blocks = ReadTextBlocks(toolCall);
        return blocks.Count == 0
            ? ""
            : string.Join(Environment.NewLine + Environment.NewLine, blocks);
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

    private static string? ReadExplicitRawInput(JsonElement toolCall)
    {
        if (toolCall.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (toolCall.TryGetProperty("rawInput", out var rawInput))
        {
            return rawInput.ValueKind == JsonValueKind.String
                ? rawInput.GetString() ?? ""
                : rawInput.GetRawText();
        }

        if (toolCall.TryGetProperty("input", out var input))
            return input.GetRawText();

        if (toolCall.TryGetProperty("arguments", out var args))
            return args.GetRawText();

        return null;
    }

    private static bool HasDiff(JsonElement toolCall)
    {
        if (toolCall.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !toolCall.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return content.EnumerateArray().Any(item => GetString(item, "type") == "diff");
    }

    /// <summary>
    /// Document body in original content order: text blocks and formatted diffs.
    /// </summary>
    private static string BuildDocumentText(JsonElement toolCall)
    {
        var parts = new List<string>();
        if (toolCall.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !toolCall.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

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
            else if (itemType == "diff")
            {
                var formatted = FormatDiffBlock(item);
                if (!string.IsNullOrWhiteSpace(formatted))
                    parts.Add(formatted);
            }
        }

        return parts.Count == 0
            ? ""
            : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    internal static string FormatDiffBlock(JsonElement item)
    {
        var path = GetString(item, "path");
        var oldText = GetDiffText(item, "oldText");
        var newText = GetDiffText(item, "newText");

        if (string.IsNullOrWhiteSpace(oldText)
            && string.IsNullOrWhiteSpace(newText)
            && string.IsNullOrWhiteSpace(path))
        {
            return "(diff)";
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.Append("### ").Append(path);
            builder.Append(Environment.NewLine).Append(Environment.NewLine);
        }

        builder.Append("```diff").Append(Environment.NewLine);
        foreach (var line in SplitPreservingEmpty(oldText))
            builder.Append('-').Append(line).Append(Environment.NewLine);
        foreach (var line in SplitPreservingEmpty(newText))
            builder.Append('+').Append(line).Append(Environment.NewLine);
        builder.Append("```");
        return builder.ToString();
    }

    private static string GetDiffText(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static IEnumerable<string> SplitPreservingEmpty(string text)
    {
        if (text.Length == 0)
            return Array.Empty<string>();

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static List<string> ReadTextBlocks(JsonElement toolCall)
    {
        var parts = new List<string>();
        if (toolCall.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !toolCall.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return parts;
        }

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

        return parts;
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
