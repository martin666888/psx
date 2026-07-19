namespace PSX.Services;

public interface IAgentWorkspaceSession : IDisposable
{
    Guid WorkspaceId { get; }
    string ThreadId { get; }
    string ProviderKey { get; }
    string WorkingDirectory { get; }
    bool IsDraft { get; }
    Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null);
    Task ClearAsync();
    Task CancelAsync();
    Task ChangeDirectoryAsync(string path);
    Task ListThreadsAsync();
    Task PublishStateAsync();
    Task RestoreAsync();
}
