namespace PSX.Services;

public interface IAgentWorkspaceSession : IDisposable
{
    Guid WorkspaceId { get; }
    string ThreadId { get; }
    string ProviderKey { get; }
    string WorkingDirectory { get; }
    bool IsDraft { get; }
    Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null);
    Task CancelAsync();
    Task ChangeDirectoryAsync(string path);
    Task ListThreadsAsync(string? requestId = null);
    Task PublishStateAsync();
    Task RestoreAsync();

    /// <summary>
    /// Re-publishes provider runtime state and resumes work that was blocked
    /// specifically because this session's runtime was unavailable.
    /// Coordinators call this only for workspaces that share the runtime which
    /// became ready; unrelated provider runtimes remain isolated.
    /// </summary>
    Task OnRuntimeReadyAsync() => PublishStateAsync();
}
