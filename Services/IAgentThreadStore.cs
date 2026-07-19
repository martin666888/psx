using PSX.Models;

namespace PSX.Services;

public interface IAgentThreadStore
{
    string RootDirectory { get; }
    string AttachmentsDirectory { get; }
    AgentThread CreateThread(string workingDirectory);
    AgentThread? LoadThread(string threadId);
    IReadOnlyList<AgentThreadSummary> ListThreads();
    int DeleteEmptyDrafts();
    void SaveThread(AgentThread thread);
    void DeleteThread(string threadId);
    void SaveLastThread(AgentThread thread);
    AgentAttachment SaveAttachment(string threadId, string fileName, string mimeType, byte[] data);
    AgentAttachment? LoadAttachment(string threadId, string attachmentId);
    IReadOnlyList<AgentAttachment> LoadAttachments(string threadId, IEnumerable<string> attachmentIds);
    void DeleteThreadAttachments(string threadId);
}
