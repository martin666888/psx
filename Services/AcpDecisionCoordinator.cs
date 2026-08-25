using System.Collections.Concurrent;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

internal enum AcpDecisionPresentation
{
    Ordinary,
    Document,
    ModeTransition
}

internal sealed record AcpDocumentDecisionRequest(
    AcpDecisionPresentation Presentation,
    string DecisionSnapshotId,
    string RequestId,
    string ToolCallId,
    string Title,
    string DocumentText,
    IReadOnlyList<AgentDecisionOption> Options);

internal sealed record AcpDocumentDecisionStateChange(
    string RequestId,
    AcpDecisionPresentation Presentation,
    string State,
    string? SelectedOptionId,
    string DecisionSnapshotId);

/// <summary>
/// Owns ACP permission and elicitation request lifetimes, including response
/// pairing, cancellation, and conversion back to ACP result payloads.
/// Document projection remains a session concern and is reached only through
/// explicit callbacks.
/// </summary>
internal sealed class AcpDecisionCoordinator
{
    private sealed class PendingPermission
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AgentDecisionOption> Options { get; init; } = Array.Empty<AgentDecisionOption>();
        public AcpDecisionPresentation Presentation { get; init; }
        public string DecisionSnapshotId { get; init; } = "";
        public bool IsAskUserForm { get; init; }
        public IReadOnlyList<int> AskUserAnswerIndexes { get; init; } = Array.Empty<int>();
        public string? FormSubmitOptionId { get; init; }
        public bool IsDocumentDecision => Presentation is AcpDecisionPresentation.Document or AcpDecisionPresentation.ModeTransition;
    }

    private sealed class PendingElicitation
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IAgentBridgeService _bridgeService;
    private readonly string _assistantName;
    private readonly Action<string> _markDocumentDecisionTool;
    private readonly Action<AcpDocumentDecisionRequest> _upsertDocumentDecision;
    private readonly Action<AcpDocumentDecisionStateChange> _updateDocumentDecision;
    private readonly ConcurrentDictionary<string, PendingPermission> _pendingPermissions = new();
    private readonly ConcurrentDictionary<string, PendingElicitation> _pendingElicitations = new();

    public AcpDecisionCoordinator(
        IAgentBridgeService bridgeService,
        string assistantName,
        Action<string> markDocumentDecisionTool,
        Action<AcpDocumentDecisionRequest> upsertDocumentDecision,
        Action<AcpDocumentDecisionStateChange> updateDocumentDecision)
    {
        _bridgeService = bridgeService ?? throw new ArgumentNullException(nameof(bridgeService));
        _assistantName = string.IsNullOrWhiteSpace(assistantName) ? "Agent" : assistantName;
        _markDocumentDecisionTool = markDocumentDecisionTool ?? throw new ArgumentNullException(nameof(markDocumentDecisionTool));
        _upsertDocumentDecision = upsertDocumentDecision ?? throw new ArgumentNullException(nameof(upsertDocumentDecision));
        _updateDocumentDecision = updateDocumentDecision ?? throw new ArgumentNullException(nameof(updateDocumentDecision));
    }

    public bool ResolvePermission(string requestId, string value)
    {
        return !string.IsNullOrWhiteSpace(requestId)
               && _pendingPermissions.TryRemove(requestId, out var pending)
               && pending.Completion.TrySetResult(value);
    }

    public bool ResolveElicitation(string requestId, string? value)
    {
        return !string.IsNullOrWhiteSpace(requestId)
               && _pendingElicitations.TryRemove(requestId, out var pending)
               && pending.Completion.TrySetResult(value ?? "{\"action\":\"cancel\"}");
    }

    public async Task<object?> HandlePermissionRequestAsync(JsonElement request, JsonElement parameters)
    {
        var requestId = request.GetProperty("id").ToString();
        var options = AcpPermissionPolicy.ReadOptions(parameters);
        var toolCall = parameters.TryGetProperty("toolCall", out var value) ? value : default;
        var toolCallId = GetString(toolCall, "toolCallId");
        var classified = AcpPermissionPolicy.Classify(toolCall);
        var presentation = classified.Presentation switch
        {
            AcpPermissionPresentation.ModeTransition => AcpDecisionPresentation.ModeTransition,
            AcpPermissionPresentation.Document => AcpDecisionPresentation.Document,
            _ => AcpDecisionPresentation.Ordinary
        };

        var explicitToolInput = classified.ExplicitRawInput;
        var description = classified.Description;
        var isAskUserForm = false;
        object? askUserSchema = null;
        string? askUserMessage = null;
        IReadOnlyList<int> askUserIndexes = Array.Empty<int>();
        string? formSubmitOptionId = null;
        if (presentation == AcpDecisionPresentation.Ordinary
            && AcpAskUserQuestionAdapter.TryCreateForm(
                toolCall,
                out askUserMessage,
                out askUserSchema,
                out askUserIndexes))
        {
            formSubmitOptionId = AcpAskUserQuestionAdapter.ResolveProceedOptionId(options);
            if (!string.IsNullOrWhiteSpace(formSubmitOptionId))
            {
                isAskUserForm = true;
                explicitToolInput = null;
                description = "";
            }
        }

        var pending = new PendingPermission
        {
            Options = options,
            Presentation = presentation,
            DecisionSnapshotId = Guid.NewGuid().ToString("N"),
            IsAskUserForm = isAskUserForm,
            AskUserAnswerIndexes = askUserIndexes,
            FormSubmitOptionId = formSubmitOptionId
        };
        _pendingPermissions[requestId] = pending;

        var title = GetString(toolCall, "title");
        if (string.IsNullOrWhiteSpace(title))
            title = GetString(toolCall, "name");

        if (pending.IsDocumentDecision)
        {
            _markDocumentDecisionTool(toolCallId);
            _upsertDocumentDecision(new(
                pending.Presentation,
                pending.DecisionSnapshotId,
                requestId,
                toolCallId,
                title,
                classified.DocumentText,
                options));
        }

        await _bridgeService.SendEventAsync(new
        {
            type = "permission_request",
            requestId,
            decisionSnapshotId = pending.DecisionSnapshotId,
            title,
            text = explicitToolInput,
            description = pending.IsDocumentDecision || string.IsNullOrWhiteSpace(description) ? null : description,
            presentation = pending.IsAskUserForm
                ? "form"
                : pending.Presentation switch
                {
                    AcpDecisionPresentation.ModeTransition => "mode_transition",
                    AcpDecisionPresentation.Document => "document",
                    _ => null
                },
            message = pending.IsAskUserForm ? askUserMessage : null,
            schema = pending.IsAskUserForm ? askUserSchema : null,
            formSubmitOptionId = pending.IsAskUserForm ? formSubmitOptionId : null,
            toolCallId,
            toolKind = GetString(toolCall, "kind"),
            toolStatus = GetString(toolCall, "status"),
            documentText = pending.IsDocumentDecision ? classified.DocumentText : null,
            options = options.Select(option => new
            {
                optionId = option.OptionId,
                name = option.Name,
                kind = option.Kind
            }).ToArray()
        }).ConfigureAwait(false);

        if (options.Length == 0)
        {
            _pendingPermissions.TryRemove(requestId, out _);
            SetDocumentState(requestId, pending, "cancelled");
            await SendPermissionCancelledAsync(requestId, "The ACP Agent did not provide any response options.")
                .ConfigureAwait(false);
            return CancelledOutcome();
        }

        var selectedRaw = await pending.Completion.Task.ConfigureAwait(false);
        if (selectedRaw == "__cancelled__")
        {
            SetDocumentState(requestId, pending, "cancelled");
            return CancelledOutcome();
        }

        if (!AcpAskUserQuestionAdapter.TryParsePermissionResponseValue(
                selectedRaw,
                out var selected,
                out var formContent,
                out var hasFormContent))
        {
            SetDocumentState(requestId, pending, "cancelled");
            await SendPermissionCancelledAsync(requestId, "The selected option was not offered by the ACP Agent.")
                .ConfigureAwait(false);
            return CancelledOutcome();
        }

        var selectedOption = AcpPermissionPolicy.FindOfferedOption(pending.Options, selected);
        if (selectedOption == null)
        {
            SetDocumentState(requestId, pending, "cancelled");
            await SendPermissionCancelledAsync(requestId, "The selected option was not offered by the ACP Agent.")
                .ConfigureAwait(false);
            return CancelledOutcome();
        }

        selected = selectedOption.OptionId;
        SetDocumentState(requestId, pending, "selected", selected);
        await _bridgeService.SendEventAsync(new
        {
            type = "permission_resolved",
            requestId,
            optionId = selected,
            optionName = selectedOption.Name
        }).ConfigureAwait(false);

        Dictionary<string, string>? answers = null;
        if (pending.IsAskUserForm
            && hasFormContent
            && string.Equals(selected, pending.FormSubmitOptionId, StringComparison.OrdinalIgnoreCase))
        {
            answers = AcpAskUserQuestionAdapter.MapContentToAnswers(formContent, pending.AskUserAnswerIndexes);
        }

        if (answers is { Count: > 0 })
        {
            return new Dictionary<string, object?>
            {
                ["outcome"] = new Dictionary<string, object?>
                {
                    ["outcome"] = "selected",
                    ["optionId"] = selected
                },
                ["answers"] = answers
            };
        }

        return new { outcome = new { outcome = "selected", optionId = selected } };
    }

    public async Task<object?> HandleElicitationCreateAsync(JsonElement request, JsonElement parameters)
    {
        var requestId = request.GetProperty("id").ToString();
        var pending = new PendingElicitation();
        _pendingElicitations[requestId] = pending;

        await _bridgeService.SendEventAsync(new
        {
            type = "elicitation_request",
            requestId,
            mode = GetString(parameters, "mode"),
            message = GetString(parameters, "message", $"{_assistantName} needs more information."),
            schema = parameters.TryGetProperty("requestedSchema", out var schema) ? JsonElementToObject(schema) : null,
            url = GetString(parameters, "url")
        }).ConfigureAwait(false);

        var responseJson = await pending.Completion.Task.ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var action = GetString(root, "action", "cancel");
            if (action is "decline" or "cancel")
                return new { action };

            return root.TryGetProperty("content", out var content)
                ? new { action = "accept", content = (object?)JsonElementToObject(content) }
                : new { action = "accept", content = (object?)new { } };
        }
        catch (JsonException)
        {
            return new { action = "cancel" };
        }
    }

    public async Task CancelAllAsync(string permissionText, string elicitationText)
    {
        foreach (var item in _pendingPermissions.ToArray())
        {
            if (!_pendingPermissions.TryRemove(item.Key, out var pending))
                continue;
            pending.Completion.TrySetResult("__cancelled__");
            SetDocumentState(item.Key, pending, "cancelled");
            await SendPermissionCancelledAsync(item.Key, permissionText).ConfigureAwait(false);
        }

        foreach (var item in _pendingElicitations.ToArray())
        {
            if (!_pendingElicitations.TryRemove(item.Key, out var pending))
                continue;
            pending.Completion.TrySetResult("{\"action\":\"cancel\"}");
            await _bridgeService.SendEventAsync(new
            {
                type = "elicitation_cancelled",
                requestId = item.Key,
                text = elicitationText
            }).ConfigureAwait(false);
        }
    }

    public void CancelAllWithoutEvents()
    {
        foreach (var item in _pendingPermissions.ToArray())
        {
            if (_pendingPermissions.TryRemove(item.Key, out var pending))
                pending.Completion.TrySetResult("__cancelled__");
        }

        foreach (var item in _pendingElicitations.ToArray())
        {
            if (_pendingElicitations.TryRemove(item.Key, out var pending))
                pending.Completion.TrySetResult("{\"action\":\"cancel\"}");
        }
    }

    private void SetDocumentState(
        string requestId,
        PendingPermission pending,
        string state,
        string? selectedOptionId = null)
    {
        if (!pending.IsDocumentDecision)
            return;
        _updateDocumentDecision(new(
            requestId,
            pending.Presentation,
            state,
            selectedOptionId,
            pending.DecisionSnapshotId));
    }

    private Task SendPermissionCancelledAsync(string requestId, string text)
        => _bridgeService.SendEventAsync(new { type = "permission_cancelled", requestId, text });

    private static object CancelledOutcome() => new { outcome = new { outcome = "cancelled" } };

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => JsonElementToObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue) ? doubleValue : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
