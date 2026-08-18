using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class AgentThreadStoreTests
{
    [TestMethod]
    public void DeleteEmptyDrafts_RemovesOnlyOrphanDraftFiles()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(DeleteEmptyDrafts_RemovesOnlyOrphanDraftFiles));
        var store = new AgentThreadStore(temporaryDirectory.Path);
        var draft = store.CreateThread(temporaryDirectory.Path);
        var visible = store.CreateThread(temporaryDirectory.Path);
        visible.Messages.Add(new AgentMessage { Role = "user", Text = "keep" });
        store.SaveThread(visible);

        var removed = store.DeleteEmptyDrafts();

        Assert.AreEqual(1, removed);
        Assert.IsNull(store.LoadThread(draft.ThreadId));
        Assert.IsNotNull(store.LoadThread(visible.ThreadId));
    }

    [TestMethod]
    public void ThinkingNormalization_RemainsStableAcrossSaveAndReload()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(ThinkingNormalization_RemainsStableAcrossSaveAndReload));
        var store = new AgentThreadStore(temporaryDirectory.Path);
        var thread = store.CreateThread(temporaryDirectory.Path);
        thread.Messages =
        [
            new AgentMessage { Role = "user", Text = "Question" },
            new AgentMessage { Role = "assistant", Text = "Before" },
            new AgentMessage { Role = "thinking", Text = "First" },
            new AgentMessage { Role = "tool", Text = "Tool", ToolCallId = "tool-1" },
            new AgentMessage { Role = "thinking", Text = "Second" }
        ];

        Assert.IsTrue(ThinkingMessageNormalizer.Normalize(thread.Messages));
        store.SaveThread(thread);

        var loaded = store.LoadThread(thread.ThreadId);
        Assert.IsNotNull(loaded);
        Assert.IsFalse(ThinkingMessageNormalizer.Normalize(loaded.Messages));
        CollectionAssert.AreEqual(
            new[] { "user", "thinking", "assistant", "tool" },
            loaded.Messages.Select(message => message.Role).ToArray());
        Assert.AreEqual("First\n\nSecond", loaded.Messages[1].Text);
        Assert.AreEqual(loaded.Messages[0].RunId, loaded.Messages[1].RunId);
    }

    [TestMethod]
    public void CreateSaveLoadAndDelete_UsesOnlyConfiguredRoot()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(CreateSaveLoadAndDelete_UsesOnlyConfiguredRoot));
        var store = new AgentThreadStore(temporaryDirectory.Path);

        var thread = store.CreateThread(temporaryDirectory.Path);
        thread.Messages.Add(new AgentMessage { Role = "user", Text = "Hello" });
        store.SaveThread(thread);

        var loaded = store.LoadThread(thread.ThreadId);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(temporaryDirectory.Path, store.RootDirectory);
        Assert.AreEqual("Hello", loaded.Messages.Single().Text);

        store.DeleteThread(thread.ThreadId);

        Assert.IsNull(store.LoadThread(thread.ThreadId));
        Assert.IsFalse(Directory.Exists(Path.Combine(store.AttachmentsDirectory, thread.ThreadId)));
    }

    [TestMethod]
    public void ListThreads_SortsVisibleThreadsByMostRecentUpdate()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(ListThreads_SortsVisibleThreadsByMostRecentUpdate));
        var store = new AgentThreadStore(temporaryDirectory.Path);
        var first = store.CreateThread(temporaryDirectory.Path);
        first.Messages.Add(new AgentMessage { Role = "user", Text = "First" });
        store.SaveThread(first);
        Thread.Sleep(20);
        var second = store.CreateThread(temporaryDirectory.Path);
        second.Messages.Add(new AgentMessage { Role = "user", Text = "Second" });
        store.SaveThread(second);

        var result = store.ListThreads();

        Assert.HasCount(2, result);
        Assert.AreEqual(second.ThreadId, result[0].ThreadId);
        Assert.AreEqual(first.ThreadId, result[1].ThreadId);
    }

    [TestMethod]
    public void LoadThread_DeserializesLegacyJsonWithMissingOptionalFields()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(LoadThread_DeserializesLegacyJsonWithMissingOptionalFields));
        var store = new AgentThreadStore(temporaryDirectory.Path);
        var threadDirectory = Path.Combine(temporaryDirectory.Path, "agent", "threads");
        Directory.CreateDirectory(threadDirectory);
        var threadId = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(threadDirectory, threadId + ".json"), $$"""
            {
              "threadId": "{{threadId}}",
              "title": "Legacy thread",
              "cwd": "C:\\legacy",
              "provider": "claude-cli",
              "messages": [
                { "role": "assistant", "text": "hello" }
              ]
            }
            """);

        var loaded = store.LoadThread(threadId);

        Assert.IsNotNull(loaded);
        Assert.IsNull(loaded.AcpSessionId);
        Assert.IsNull(loaded.Messages[0].DecisionOptions);
        Assert.IsNull(loaded.Messages[0].DecisionState);
    }

    [TestMethod]
    public void ListThreads_CorruptThreadLeavesExistingIndexUntouched()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(ListThreads_CorruptThreadLeavesExistingIndexUntouched));
        var store = new AgentThreadStore(temporaryDirectory.Path);
        var thread = store.CreateThread(temporaryDirectory.Path);
        thread.Messages.Add(new AgentMessage { Role = "user", Text = "Keep index" });
        store.SaveThread(thread);
        var indexPath = Path.Combine(temporaryDirectory.Path, "agent", "index.json");
        var indexBefore = File.ReadAllText(indexPath);
        var threadPath = Path.Combine(temporaryDirectory.Path, "agent", "threads", $"{thread.ThreadId}.json");
        File.WriteAllText(threadPath, "{ invalid json");

        Assert.ThrowsExactly<InvalidDataException>(() => store.ListThreads());

        Assert.AreEqual(indexBefore, File.ReadAllText(indexPath));
    }

    [TestMethod]
    public void Attachments_AreIsolatedAndCleanedPerThread()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(Attachments_AreIsolatedAndCleanedPerThread));
        var store = new AgentThreadStore(temporaryDirectory.Path);

        var first = store.SaveAttachment("thread-one", "..\\unsafe.png", "image/png", [1, 2, 3]);
        var second = store.SaveAttachment("thread-two", "safe.png", "image/png", [4, 5]);

        StringAssert.StartsWith(first.Path, Path.Combine(store.AttachmentsDirectory, "thread-one"));
        Assert.AreEqual("unsafe.png", first.FileName);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first.Path));
        Assert.IsNotNull(store.LoadAttachment("thread-one", first.Id));
        Assert.IsNull(store.LoadAttachment("thread-one", second.Id));

        store.DeleteThreadAttachments("thread-one");

        Assert.IsFalse(Directory.Exists(Path.Combine(store.AttachmentsDirectory, "thread-one")));
        Assert.IsTrue(File.Exists(second.Path));
    }

    [TestMethod]
    public void LoadThread_RejectsPathTraversalThreadId()
    {
        using var temporaryDirectory = TestWorkspace.Create(nameof(LoadThread_RejectsPathTraversalThreadId));
        var store = new AgentThreadStore(temporaryDirectory.Path);

        Assert.ThrowsExactly<InvalidOperationException>(() => store.LoadThread(@"..\escape"));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.LoadThread(@"C:\Windows\notepad"));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.DeleteThread("not-a-guid"));
    }

}
