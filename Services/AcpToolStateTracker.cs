using System.Text.Json;

namespace PSX.Services;

internal sealed record AcpToolSnapshot(
    string ToolCallId,
    string Name,
    string Summary,
    string Input,
    string Output,
    string Status);

internal sealed record AcpToolUpdateResult(
    bool Suppressed,
    bool Started,
    bool SnapshotChanged,
    string? TerminalDelta,
    bool IsTerminal,
    AcpToolSnapshot? StartSnapshot,
    AcpToolSnapshot? Snapshot);

/// <summary>
/// Owns the live tool-call state machine. Callers provide synchronization and
/// perform bridge/persistence side effects from the immutable snapshots
/// returned by this class.
/// </summary>
internal sealed class AcpToolStateTracker
{
    private readonly Dictionary<string, string> _names = new();
    private readonly Dictionary<string, string> _summaries = new();
    private readonly Dictionary<string, string> _inputs = new();
    private readonly Dictionary<string, string> _outputs = new();
    private readonly Dictionary<string, string> _pendingParamSnapshots = new();
    private readonly HashSet<string> _startedCallIds = new();
    private readonly HashSet<string> _documentDecisionCallIds = new();

    public void Clear()
    {
        _names.Clear();
        _summaries.Clear();
        _inputs.Clear();
        _outputs.Clear();
        _pendingParamSnapshots.Clear();
        _startedCallIds.Clear();
        _documentDecisionCallIds.Clear();
    }

    public void MarkDocumentDecision(string toolCallId)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId))
            _documentDecisionCallIds.Add(toolCallId);
    }

    public AcpToolUpdateResult ApplyUpdate(
        JsonElement update,
        Func<JsonElement, string> resolveToolName)
    {
        ArgumentNullException.ThrowIfNull(resolveToolName);

        var toolCallId = GetString(update, "toolCallId");
        if (IsDocumentDecision(toolCallId))
        {
            RemoveTrackedState(toolCallId);
            return new(true, false, false, null, false, null, null);
        }

        var resolvedName = resolveToolName(update);
        var input = FormatInput(update);
        if (!_names.ContainsKey(toolCallId))
            _names[toolCallId] = resolvedName;
        else if (string.Equals(_names[toolCallId], "Tool", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(resolvedName, "Tool", StringComparison.OrdinalIgnoreCase))
            _names[toolCallId] = resolvedName;

        if (!string.IsNullOrEmpty(input) || update.TryGetProperty("rawInput", out _))
            _inputs[toolCallId] = input;
        if (!_inputs.ContainsKey(toolCallId))
            _inputs[toolCallId] = "";
        if (!_outputs.ContainsKey(toolCallId))
            _outputs[toolCallId] = "";
        if (!_summaries.ContainsKey(toolCallId))
            _summaries[toolCallId] = BuildSummary(_names[toolCallId], _inputs[toolCallId], update);

        var started = _startedCallIds.Add(toolCallId);
        var startSnapshot = started ? CreateSnapshot(toolCallId, "running") : null;
        var snapshotChanged = false;
        var terminalChunk = ReadTerminalOutputChunk(update);
        var hasRawInput = update.TryGetProperty("rawInput", out _);
        var hasContent = update.TryGetProperty("content", out _);
        var hasRawOutput = update.TryGetProperty("rawOutput", out _);
        var status = GetString(update, "status");
        var isTerminal = status is "completed" or "failed";
        string? terminalDelta = null;

        var presentTitle = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(presentTitle))
        {
            if (!string.Equals(_names[toolCallId], presentTitle, StringComparison.Ordinal))
            {
                _names[toolCallId] = presentTitle;
                snapshotChanged = true;
            }

            var titledSummary = BuildSummary(presentTitle, _inputs[toolCallId], update);
            if (!string.Equals(_summaries[toolCallId], titledSummary, StringComparison.Ordinal))
            {
                _summaries[toolCallId] = titledSummary;
                snapshotChanged = true;
            }
        }

        if (hasRawInput)
        {
            _inputs[toolCallId] = input;
            _pendingParamSnapshots.Remove(toolCallId);
            _summaries[toolCallId] = BuildSummary(_names[toolCallId], input, update);
            snapshotChanged = true;
        }

        if (hasContent || hasRawOutput)
        {
            var displayOutput = FormatContentAndRawOutput(update);
            if (!isTerminal
                && !hasRawInput
                && string.IsNullOrWhiteSpace(_inputs[toolCallId])
                && IsPendingParamSnapshot(displayOutput))
            {
                _pendingParamSnapshots[toolCallId] = displayOutput;
            }
            else
            {
                _pendingParamSnapshots.Remove(toolCallId);
                _outputs[toolCallId] = displayOutput;
                snapshotChanged = true;
            }
        }
        else if (!string.IsNullOrEmpty(terminalChunk))
        {
            _outputs[toolCallId] += terminalChunk;
            terminalDelta = terminalChunk;
        }

        return new(
            false,
            started,
            snapshotChanged,
            terminalDelta,
            isTerminal,
            startSnapshot,
            CreateSnapshot(toolCallId, string.IsNullOrWhiteSpace(status) ? "running" : status));
    }

    public AcpToolSnapshot? Complete(string toolCallId, string status)
    {
        if (IsDocumentDecision(toolCallId))
        {
            RemoveTrackedState(toolCallId);
            return null;
        }

        var snapshot = CreateSnapshot(toolCallId, status);
        if (string.IsNullOrWhiteSpace(snapshot.Output)
            && _pendingParamSnapshots.TryGetValue(toolCallId, out var pending)
            && !IsPendingParamSnapshot(pending))
        {
            snapshot = snapshot with { Output = pending };
        }

        RemoveTrackedState(toolCallId);
        return snapshot;
    }

    public AcpToolSnapshot GetSnapshot(string toolCallId, string? status = null)
        => CreateSnapshot(toolCallId, string.IsNullOrWhiteSpace(status) ? "running" : status);

    private bool IsDocumentDecision(string toolCallId)
        => !string.IsNullOrWhiteSpace(toolCallId) && _documentDecisionCallIds.Contains(toolCallId);

    private AcpToolSnapshot CreateSnapshot(string toolCallId, string status)
    {
        var name = _names.GetValueOrDefault(toolCallId, "Tool");
        return new(
            toolCallId,
            name,
            _summaries.GetValueOrDefault(toolCallId, name),
            _inputs.GetValueOrDefault(toolCallId, ""),
            _outputs.GetValueOrDefault(toolCallId, ""),
            status);
    }

    private void RemoveTrackedState(string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
            return;

        _names.Remove(toolCallId);
        _summaries.Remove(toolCallId);
        _inputs.Remove(toolCallId);
        _outputs.Remove(toolCallId);
        _pendingParamSnapshots.Remove(toolCallId);
        _startedCallIds.Remove(toolCallId);
    }

    public static string ReadStandardName(JsonElement update)
    {
        var title = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        var kind = GetString(update, "kind");
        return string.IsNullOrWhiteSpace(kind) ? "Tool" : kind;
    }

    public static string FormatInput(JsonElement update)
    {
        if (!update.TryGetProperty("rawInput", out var rawInput))
            return "";

        return rawInput.ValueKind == JsonValueKind.String
            ? rawInput.GetString() ?? ""
            : rawInput.GetRawText();
    }

    public static string ReadTerminalOutputChunk(JsonElement update)
    {
        if (!TryReadNested(update, out var terminalOutput, "_meta", "terminal_output"))
            return "";
        return GetString(terminalOutput, "data");
    }

    public static string FormatContentAndRawOutput(JsonElement update)
    {
        var contentText = FormatContentBlocks(update);
        if (!string.IsNullOrWhiteSpace(contentText))
            return contentText;

        if (!update.TryGetProperty("rawOutput", out var rawOutput))
            return "";
        if (rawOutput.ValueKind == JsonValueKind.String)
            return rawOutput.GetString() ?? "";
        return rawOutput.ValueKind == JsonValueKind.Null ? "" : rawOutput.GetRawText();
    }

    public static bool IsPendingParamSnapshot(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    public static string BuildSummary(string name, string input, JsonElement update)
    {
        var title = GetString(update, "title");
        if (!string.IsNullOrWhiteSpace(title))
            return title.Length <= 100 ? title : title[..97] + "...";

        if (string.IsNullOrWhiteSpace(input))
            return name;

        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            var detail = GetString(root, "file_path");
            if (string.IsNullOrWhiteSpace(detail))
                detail = GetString(root, "path");
            if (string.IsNullOrWhiteSpace(detail))
                detail = GetString(root, "command");
            if (string.IsNullOrWhiteSpace(detail))
                detail = GetString(root, "description");
            if (string.IsNullOrWhiteSpace(detail))
                return name;

            var summary = name + " " + detail;
            return summary.Length <= 100 ? summary : summary[..97] + "...";
        }
        catch (JsonException)
        {
            return name;
        }
    }

    private static string FormatContentBlocks(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            var itemType = GetString(item, "type");
            if (itemType == "content"
                && item.TryGetProperty("content", out var block)
                && GetString(block, "type") == "text")
            {
                parts.Add(GetString(block, "text"));
            }
            else if (itemType == "text")
            {
                parts.Add(GetString(item, "text"));
            }
            else if (itemType == "diff")
            {
                parts.Add(item.GetRawText());
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool TryReadNested(JsonElement element, out JsonElement result, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                result = default;
                return false;
            }
        }

        result = current;
        return true;
    }
}
