namespace PSX.Models;

public enum WorkspaceKind
{
    Terminal,
    Agent,
    DshWeb,
    KimiWeb
}

/// <summary>
/// Maps a workspace kind to its wire value. The wire contract is explicit:
/// <c>DshWeb</c> serializes as <c>dsh_web</c> and <c>KimiWeb</c> as
/// <c>kimi_web</c>, never the enum-name lowercasing (<c>dshweb</c> /
/// <c>kimiweb</c>). Catalog and layout payloads must both go through here.
/// </summary>
internal static class WorkspaceWireKind
{
    public static string ToWire(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Terminal => "terminal",
        WorkspaceKind.Agent => "agent",
        WorkspaceKind.DshWeb => "dsh_web",
        WorkspaceKind.KimiWeb => "kimi_web",
        _ => kind.ToString().ToLowerInvariant()
    };
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
