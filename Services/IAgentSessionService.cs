namespace PSX.Services;

public interface IAgentSessionService : IDisposable
{
    Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null);
    Task ClearAsync();
    Task CancelAsync();
    Task NewThreadAsync(string? workingDirectory = null);
    Task LoadThreadAsync(string threadId);
    Task DeleteThreadAsync(string threadId);
    Task ChangeDirectoryAsync(string path);
    Task ListThreadsAsync();
    Task PublishStateAsync();
}
