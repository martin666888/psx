namespace PSX.Models;

public enum WorkspaceKind
{
    Terminal,
    Agent
}

public enum AgentWorkspaceState
{
    Draft,
    Idle,
    Running,
    WaitingForInput,
    WaitingForPermission,
    Error,
    TranscriptOnly
}

public enum WorkspaceCloseReason
{
    User,
    ThreadDeleted,
    ApplicationShutdown
}

public sealed class WorkspaceDescriptor
{
    public Guid WorkspaceId { get; init; }
    public WorkspaceKind Kind { get; init; }
    public string Title { get; set; } = "Workspace";
    public string IconKey { get; init; } = "terminal";
    public AgentWorkspaceState? AgentState { get; set; }
    public string? ProviderKey { get; init; }
    public string? ProviderName { get; set; }
    public string? ThreadId { get; init; }
    public string? WorkingDirectory { get; set; }
}

public sealed record AgentProviderCatalogItem(
    string Key,
    string DisplayName,
    string AssistantName,
    bool IsDefault,
    string IconKey = "agent");

public sealed class AgentProviderOptions
{
    public string DefaultProviderKey { get; init; } = "acp-claude";
}
