namespace PSX.Models;

public sealed class AgentMessage
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    // Folding/grouping fields (all nullable for backward-compatible JSON deserialization)
    public string? RunId { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolOutput { get; set; }
    public string? ToolStatus { get; set; }
    public string? Summary { get; set; }
    public List<AgentPlanEntry>? PlanEntries { get; set; }
    public string? RequestId { get; set; }
    public string? DecisionState { get; set; }
    public string? SelectedOptionId { get; set; }
    public List<AgentDecisionOption>? DecisionOptions { get; set; }
    public List<AgentAttachment>? Attachments { get; set; }
}

public sealed class AgentDecisionOption
{
    public string OptionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class AgentPlanEntry
{
    public string Content { get; set; } = "";
    public string Status { get; set; } = "";
    public string Priority { get; set; } = "";
}

public sealed class AgentAttachment
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public string Uri { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
