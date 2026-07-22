using PSX.Models;

namespace PSX.Services;

internal static class AgentThreadBridgePayload
{
    public static object ThreadLoaded(AgentThread thread, bool clear, bool selectPlan)
    {
        return new
        {
            type = "agent_thread_loaded",
            clear,
            selectPlan,
            threadId = thread.ThreadId,
            title = thread.Title,
            cwd = thread.Cwd,
            sessionId = thread.AcpSessionId ?? thread.ClaudeSessionId ?? "",
            contextUsedTokens = thread.ContextUsedTokens,
            contextWindowTokens = thread.ContextWindowTokens,
            contextCostAmount = thread.ContextCostAmount,
            contextCostCurrency = thread.ContextCostCurrency,
            messages = thread.Messages.Select(message => new
            {
                role = message.Role,
                text = message.Text,
                name = message.Name ?? "",
                createdAt = message.CreatedAt.ToString("u"),
                runId = message.RunId,
                toolCallId = message.ToolCallId,
                summary = message.Summary,
                toolInput = message.ToolInput,
                toolOutput = message.ToolOutput,
                toolStatus = message.ToolStatus,
                requestId = message.RequestId,
                decisionState = message.DecisionState,
                selectedOptionId = message.SelectedOptionId,
                decisionOptions = message.DecisionOptions?.Select(option => new
                {
                    optionId = option.OptionId,
                    name = option.Name,
                    kind = option.Kind
                }).ToArray(),
                planEntries = message.PlanEntries?.Select(entry => new
                {
                    content = entry.Content,
                    status = entry.Status,
                    priority = entry.Priority
                }).ToArray(),
                attachments = message.Attachments?.Select(Attachment).ToArray()
            }).ToArray()
        };
    }

    public static object ThreadList(IReadOnlyList<AgentThreadSummary> threads)
    {
        return new
        {
            type = "agent_threads",
            threads = threads.Select(thread => new
            {
                threadId = thread.ThreadId,
                title = thread.Title,
                cwd = thread.Cwd,
                provider = thread.Provider,
                sessionId = thread.AcpSessionId ?? thread.ClaudeSessionId ?? "",
                updatedAt = thread.UpdatedAt.ToString("u")
            }).ToArray()
        };
    }

    private static object Attachment(AgentAttachment attachment)
    {
        return new
        {
            id = attachment.Id,
            fileName = attachment.FileName,
            mimeType = attachment.MimeType,
            size = attachment.Size,
            url = attachment.Url,
            uri = attachment.Uri,
            createdAt = attachment.CreatedAt.ToString("u")
        };
    }
}
