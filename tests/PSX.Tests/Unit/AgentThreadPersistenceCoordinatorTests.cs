using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentThreadPersistenceCoordinatorTests
{
    [TestMethod]
    public async Task QueueCheckpoint_OneHundredUpdatesProduceAtMostTwoPhysicalWrites()
    {
        var store = new RecordingPersistenceStore();
        using var coordinator = new AgentThreadPersistenceCoordinator(store);
        var thread = CreateThread();

        for (var index = 0; index < 100; index++)
        {
            thread.Title = $"checkpoint-{index}";
            coordinator.QueueCheckpoint(thread);
        }

        await Task.Delay(AgentThreadPersistenceCoordinator.CheckpointDelay + TimeSpan.FromMilliseconds(150));

        Assert.IsGreaterThanOrEqualTo(1, store.SaveCount);
        Assert.IsLessThanOrEqualTo(2, store.SaveCount);
        Assert.AreEqual("checkpoint-99", store.StoredThread!.Title);
        Assert.AreEqual(store.SaveCount, store.IndexWriteCount);
    }

    [TestMethod]
    public void SaveCritical_IsImmediatelyReadableAndDoesNotRetainLiveThreadReference()
    {
        var store = new RecordingPersistenceStore();
        using var coordinator = new AgentThreadPersistenceCoordinator(store);
        var thread = CreateThread();
        thread.Title = "durable";

        coordinator.SaveCritical(thread);
        thread.Title = "mutated-after-save";

        Assert.AreEqual("durable", store.LoadThread(thread.ThreadId)!.Title);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    public async Task DeleteThread_InvalidatesQueuedWritesAndBlocksAttachmentResurrection()
    {
        var store = new RecordingPersistenceStore();
        using var coordinator = new AgentThreadPersistenceCoordinator(store);
        var thread = CreateThread();
        coordinator.QueueCheckpoint(thread);

        coordinator.DeleteThread(thread.ThreadId);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            coordinator.SaveAttachment(thread.ThreadId, "image.png", "image/png", [1, 2, 3]));
        await Task.Delay(AgentThreadPersistenceCoordinator.CheckpointDelay + TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(0, store.AttachmentSaveCount);
    }

    [TestMethod]
    public async Task FlushThread_WritesLatestCheckpointAndIndexTogether()
    {
        var store = new RecordingPersistenceStore();
        using var coordinator = new AgentThreadPersistenceCoordinator(store);
        var thread = CreateThread();
        coordinator.QueueCheckpoint(thread);

        await coordinator.FlushThreadAsync(thread.ThreadId);

        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(1, store.IndexWriteCount);
        Assert.IsNotNull(store.LoadThread(thread.ThreadId));
    }

    private static AgentThread CreateThread() => new()
    {
        ThreadId = Guid.NewGuid().ToString(),
        Title = "thread",
        Cwd = Environment.CurrentDirectory,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now
    };
}

internal sealed class RecordingPersistenceStore : IAgentThreadStore
{
    public string RootDirectory => Environment.CurrentDirectory;
    public string AttachmentsDirectory => Environment.CurrentDirectory;
    public int SaveCount { get; private set; }
    public int IndexWriteCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int AttachmentSaveCount { get; private set; }
    public AgentThread? StoredThread { get; private set; }

    public AgentThread CreateThread(string workingDirectory) => throw new NotSupportedException();
    public AgentThread? LoadThread(string threadId) => StoredThread?.ThreadId == threadId ? StoredThread : null;
    public IReadOnlyList<AgentThreadSummary> ListThreads() => [];
    public AgentThreadUsageSnapshot ReadUsageSnapshot() => new([], 0, 0);
    public int DeleteEmptyDrafts() => 0;
    public void SaveThread(AgentThread thread)
    {
        SaveCount++;
        IndexWriteCount++;
        StoredThread = thread;
    }
    public void DeleteThread(string threadId)
    {
        DeleteCount++;
        StoredThread = null;
    }
    public void SaveLastThread(AgentThread thread) { }
    public AgentAttachment SaveAttachment(string threadId, string fileName, string mimeType, byte[] data)
    {
        AttachmentSaveCount++;
        return new AgentAttachment { Id = Guid.NewGuid().ToString("N"), FileName = fileName, MimeType = mimeType };
    }
    public AgentAttachment? LoadAttachment(string threadId, string attachmentId) => null;
    public IReadOnlyList<AgentAttachment> LoadAttachments(string threadId, IEnumerable<string> attachmentIds) => [];
    public void DeleteThreadAttachments(string threadId) { }
}
