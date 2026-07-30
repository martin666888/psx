using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Lifecycle contract for opening a History thread: activation must happen
/// immediately after workspace creation, before (and independently of) the
/// slow ACP restore, and the restore must never re-activate or kill the tab.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class AgentWorkspaceActivationTests
{
    private static AgentThread CreateRestorableThread(AgentThreadStore store, string cwd, string title)
    {
        var thread = store.CreateThread(cwd);
        thread.Provider = "fake-acp";
        thread.Title = title;
        thread.AcpSessionId = "fake-session-" + title;
        thread.Messages.Add(new AgentMessage { Role = "user", Text = "hello from " + title });
        thread.Messages.Add(new AgentMessage { Role = "assistant", Text = "answer for " + title });
        store.SaveThread(thread);
        return thread;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string scope, bool runtimeReady = true)
        {
            Workspace = TestWorkspace.Create(scope);
            Store = new AgentThreadStore(Path.Combine(Workspace.Path, "store"));
            Bridge = new RecordingAgentBridgeService();
            Runtime = new FakeAcpRuntime(Workspace, runtimeReady);
            var provider = new FakeAcpProvider(Runtime);
            Registry = new AgentProviderRegistry(
                [provider],
                new AgentProviderOptions { DefaultProviderKey = provider.Descriptor.Key });
            History = new AgentHistoryCatalog();
            var factory = new AgentWorkspaceFactory(
                Bridge,
                new NullTabManagementService(),
                new NullTerminalBridgeService(),
                Store,
                new NullAgentDirectoryPicker(),
                Registry,
                new AgentRuntimeCoordinator(Registry));
            Coordinator = new AgentWorkspaceCoordinator(Bridge, Store, Registry, factory, History);
        }

        public TestWorkspace Workspace { get; }
        public AgentThreadStore Store { get; }
        public RecordingAgentBridgeService Bridge { get; }
        public FakeAcpRuntime Runtime { get; }
        public AgentProviderRegistry Registry { get; }
        public AgentHistoryCatalog History { get; }
        public AgentWorkspaceCoordinator Coordinator { get; }

        public int ActivationRequestCount { get; private set; }

        public void CountActivationRequests() =>
            Coordinator.WorkspaceActivationRequested += (_, _) => ActivationRequestCount++;

        public int EventIndex(string type, Func<System.Text.Json.JsonElement, bool>? predicate = null)
        {
            for (var i = 0; i < Bridge.Events.Count; i++)
            {
                var candidate = Bridge.Events[i];
                if (!candidate.TryGetProperty("type", out var eventType) || eventType.GetString() != type)
                    continue;
                if (predicate == null || predicate(candidate))
                    return i;
            }

            return -1;
        }

        public int EventCount(string type) =>
            Bridge.Events.Count(message =>
                message.TryGetProperty("type", out var eventType) && eventType.GetString() == type);

        public void Dispose()
        {
            Coordinator.Dispose();
            History.Dispose();
            Workspace.Dispose();
        }
    }

    [TestMethod]
    public async Task OpenThread_ActivatesBeforeRestoreContentArrives()
    {
        using var fixture = new Fixture(nameof(OpenThread_ActivatesBeforeRestoreContentArrives));
        fixture.CountActivationRequests();
        var thread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "alpha");

        var workspaceId = await fixture.Coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.IsNotNull(workspaceId);
        var forWorkspace = (System.Text.Json.JsonElement message) =>
            message.TryGetProperty("workspaceId", out var id) && id.GetString() == workspaceId.ToString();
        var createdIndex = fixture.EventIndex("agent_workspace_created", forWorkspace);
        var activatedIndex = fixture.EventIndex("workspace_activated", forWorkspace);
        var loadedIndex = fixture.EventIndex("agent_thread_loaded", forWorkspace);
        var restoringIndex = fixture.EventIndex("agent_state", message =>
            forWorkspace(message)
            && message.TryGetProperty("status", out var status) && status.GetString() == "restoring");

        Assert.IsGreaterThanOrEqualTo(0, createdIndex, "agent_workspace_created was not sent.");
        Assert.IsGreaterThan(createdIndex, activatedIndex, "workspace_activated must follow agent_workspace_created.");
        Assert.IsGreaterThan(activatedIndex, loadedIndex, "the local snapshot may only arrive after activation.");
        Assert.IsGreaterThan(activatedIndex, restoringIndex, "the restoring state may only arrive after activation.");
        Assert.AreEqual(1, fixture.ActivationRequestCount, "the WPF activation request must fire exactly once.");
    }

    [TestMethod]
    public async Task OpenThread_CompletedRestore_DoesNotReactivate()
    {
        using var fixture = new Fixture(nameof(OpenThread_CompletedRestore_DoesNotReactivate));
        var thread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "beta");

        await fixture.Coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.AreEqual(
            1,
            fixture.EventCount("workspace_activated"),
            "a finished restore must not activate the workspace a second time.");
    }

    [TestMethod]
    public async Task OpenThread_SameThreadTwice_ReusesSingleWorkspace()
    {
        using var fixture = new Fixture(nameof(OpenThread_SameThreadTwice_ReusesSingleWorkspace));
        var thread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "gamma");

        var first = await fixture.Coordinator.OpenThreadAsync(thread.ThreadId);
        var second = await fixture.Coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.IsNotNull(first);
        Assert.AreEqual(first, second, "re-opening the same thread must reuse its workspace.");
        Assert.HasCount(1, fixture.Coordinator.Workspaces);
        Assert.AreEqual(2, fixture.EventCount("workspace_activated"),
            "the second open only re-activates the existing workspace.");
    }

    [TestMethod]
    public async Task OpenThread_TwoThreads_RestoreIndependently()
    {
        using var fixture = new Fixture(nameof(OpenThread_TwoThreads_RestoreIndependently));
        var first = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "delta");
        var second = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "epsilon");

        var results = await Task.WhenAll(
            fixture.Coordinator.OpenThreadAsync(first.ThreadId),
            fixture.Coordinator.OpenThreadAsync(second.ThreadId));

        Assert.IsNotNull(results[0]);
        Assert.IsNotNull(results[1]);
        Assert.AreNotEqual(results[0], results[1]);
        Assert.HasCount(2, fixture.Coordinator.Workspaces);
        Assert.AreEqual(2, fixture.EventCount("workspace_activated"));
        foreach (var workspaceId in results)
        {
            var forWorkspace = (System.Text.Json.JsonElement message) =>
                message.TryGetProperty("workspaceId", out var id) && id.GetString() == workspaceId.ToString();
            Assert.IsGreaterThanOrEqualTo(0, fixture.EventIndex("agent_thread_loaded", forWorkspace),
                "each workspace must receive its own local snapshot.");
        }
    }

    [TestMethod]
    public async Task OpenThread_UnsupportedProvider_KeepsActivatedTranscriptWorkspace()
    {
        using var fixture = new Fixture(nameof(OpenThread_UnsupportedProvider_KeepsActivatedTranscriptWorkspace));
        var thread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "zeta");
        thread.Provider = "unknown-provider";
        fixture.Store.SaveThread(thread);

        var workspaceId = await fixture.Coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.IsNotNull(workspaceId, "a transcript-only thread must still open a workspace.");
        Assert.HasCount(1, fixture.Coordinator.Workspaces);
        var forWorkspace = (System.Text.Json.JsonElement message) =>
            message.TryGetProperty("workspaceId", out var id) && id.GetString() == workspaceId.ToString();
        Assert.IsGreaterThanOrEqualTo(0, fixture.EventIndex("workspace_activated", forWorkspace),
            "the workspace must be activated even though the provider is unknown.");
        Assert.IsGreaterThanOrEqualTo(0, fixture.EventIndex("agent_thread_loaded", forWorkspace),
            "the local transcript must still render.");
        Assert.AreEqual(0, fixture.EventCount("agent_history_error"),
            "a supported degrade path must not surface as a history error.");
    }

    [TestMethod]
    public async Task InstallRuntime_RefreshesDraftAndRestoresEveryBlockedHistoryWorkspace()
    {
        using var fixture = new Fixture(
            nameof(InstallRuntime_RefreshesDraftAndRestoresEveryBlockedHistoryWorkspace),
            runtimeReady: false);
        var draftWorkspaceId = (await fixture.Coordinator.CreateAsync(
            fixture.Registry.DefaultProvider.Descriptor.Key,
            fixture.Workspace.Path))!.Value;
        var firstThread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "blocked-alpha");
        var secondThread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "blocked-beta");

        var firstWorkspaceId = (await fixture.Coordinator.OpenThreadAsync(firstThread.ThreadId))!.Value;
        var secondWorkspaceId = (await fixture.Coordinator.OpenThreadAsync(secondThread.ThreadId))!.Value;

        foreach (var workspaceId in new[] { firstWorkspaceId, secondWorkspaceId })
        {
            Assert.IsGreaterThanOrEqualTo(0, fixture.EventIndex("agent_state", message =>
                message.TryGetProperty("workspaceId", out var id) && id.GetString() == workspaceId.ToString()
                && message.TryGetProperty("status", out var status) && status.GetString() == "transcript_only"));
        }

        fixture.Bridge.RaiseCommand("install_runtime", workspaceId: secondWorkspaceId);

        foreach (var workspaceId in new[] { draftWorkspaceId, firstWorkspaceId, secondWorkspaceId })
        {
            await fixture.Bridge.WaitForEventAsync(
                "runtime_status",
                message => message.GetProperty("workspaceId").GetString() == workspaceId.ToString()
                    && message.GetProperty("state").GetString() == "ready");
        }

        foreach (var workspaceId in new[] { firstWorkspaceId, secondWorkspaceId })
        {
            await fixture.Bridge.WaitForEventAsync(
                "agent_state",
                message => message.GetProperty("workspaceId").GetString() == workspaceId.ToString()
                    && message.GetProperty("status").GetString() == "restored");
        }

        var restoredMessagesBeforeDuplicateReady = fixture.Bridge.Events.Count(message =>
            message.TryGetProperty("type", out var type) && type.GetString() == "agent_ready"
            && message.TryGetProperty("workspaceId", out var id)
            && (id.GetString() == firstWorkspaceId.ToString() || id.GetString() == secondWorkspaceId.ToString()));
        Assert.AreEqual(2, restoredMessagesBeforeDuplicateReady);

        fixture.Runtime.PublishStatus("Fake ACP runtime is still ready.");
        await Task.Yield();

        var restoredMessagesAfterDuplicateReady = fixture.Bridge.Events.Count(message =>
            message.TryGetProperty("type", out var type) && type.GetString() == "agent_ready"
            && message.TryGetProperty("workspaceId", out var id)
            && (id.GetString() == firstWorkspaceId.ToString() || id.GetString() == secondWorkspaceId.ToString()));
        Assert.AreEqual(
            restoredMessagesBeforeDuplicateReady,
            restoredMessagesAfterDuplicateReady,
            "Repeated ready notifications must not restore a history workspace twice.");
    }

    [TestMethod]
    public async Task InstallRuntime_UpdatesOnlyProvidersSharingThatRuntime()
    {
        using var workspace = TestWorkspace.Create(nameof(InstallRuntime_UpdatesOnlyProvidersSharingThatRuntime));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var firstRuntime = new FakeAcpRuntime(workspace, initiallyReady: false);
        var secondRuntime = new FakeAcpRuntime(workspace, initiallyReady: false);
        var firstProvider = new FakeAcpProvider(firstRuntime, "provider-one", "Provider One", "One");
        var secondProvider = new FakeAcpProvider(secondRuntime, "provider-two", "Provider Two", "Two");
        var registry = new AgentProviderRegistry(
            [firstProvider, secondProvider],
            new AgentProviderOptions { DefaultProviderKey = firstProvider.Descriptor.Key });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry,
            new AgentRuntimeCoordinator(registry));
        using var coordinator = new AgentWorkspaceCoordinator(bridge, store, registry, factory, history);
        var firstWorkspaceId = (await coordinator.CreateAsync(firstProvider.Descriptor.Key, workspace.Path))!.Value;
        var secondWorkspaceId = (await coordinator.CreateAsync(secondProvider.Descriptor.Key, workspace.Path))!.Value;

        bridge.RaiseCommand("install_runtime", workspaceId: firstWorkspaceId);
        await bridge.WaitForEventAsync(
            "runtime_status",
            message => message.GetProperty("workspaceId").GetString() == firstWorkspaceId.ToString()
                && message.GetProperty("state").GetString() == "ready");

        Assert.IsTrue(firstRuntime.IsReady());
        Assert.IsFalse(secondRuntime.IsReady());
        Assert.IsFalse(bridge.Events.Any(message =>
            message.TryGetProperty("type", out var type) && type.GetString() == "runtime_status"
            && message.TryGetProperty("workspaceId", out var id) && id.GetString() == secondWorkspaceId.ToString()
            && message.TryGetProperty("state", out var state) && state.GetString() == "ready"),
            "Installing one provider's packages must not mark a different runtime ready.");
    }

    /// <summary>Delegates everything except one LoadThread call, which fails.</summary>
    private sealed class ThrowingLoadThreadStore(IAgentThreadStore inner, string failingThreadId) : IAgentThreadStore
    {
        public string RootDirectory => inner.RootDirectory;
        public string AttachmentsDirectory => inner.AttachmentsDirectory;
        public AgentThread CreateThread(string workingDirectory) => inner.CreateThread(workingDirectory);
        public AgentThread? LoadThread(string threadId) =>
            string.Equals(threadId, failingThreadId, StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("Simulated thread store failure.")
                : inner.LoadThread(threadId);
        public IReadOnlyList<AgentThreadSummary> ListThreads() => inner.ListThreads();
        public AgentThreadUsageSnapshot ReadUsageSnapshot() => inner.ReadUsageSnapshot();
        public int DeleteEmptyDrafts() => inner.DeleteEmptyDrafts();
        public void SaveThread(AgentThread thread) => inner.SaveThread(thread);
        public void DeleteThread(string threadId) => inner.DeleteThread(threadId);
        public void SaveLastThread(AgentThread thread) => inner.SaveLastThread(thread);
        public AgentAttachment SaveAttachment(string threadId, string fileName, string mimeType, byte[] data) =>
            inner.SaveAttachment(threadId, fileName, mimeType, data);
        public AgentAttachment? LoadAttachment(string threadId, string attachmentId) =>
            inner.LoadAttachment(threadId, attachmentId);
        public IReadOnlyList<AgentAttachment> LoadAttachments(string threadId, IEnumerable<string> attachmentIds) =>
            inner.LoadAttachments(threadId, attachmentIds);
        public void DeleteThreadAttachments(string threadId) => inner.DeleteThreadAttachments(threadId);
    }

    [TestMethod]
    public async Task LoadThreadCommand_StoreFailure_ReportsOpenErrorInHistoryDock()
    {
        using var workspace = TestWorkspace.Create(nameof(LoadThreadCommand_StoreFailure_ReportsOpenErrorInHistoryDock));
        var store = new ThrowingLoadThreadStore(
            new AgentThreadStore(Path.Combine(workspace.Path, "store")),
            "boom");
        var bridge = new RecordingAgentBridgeService();
        var runtime = new FakeAcpRuntime(workspace);
        var provider = new FakeAcpProvider(runtime);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = provider.Descriptor.Key });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry,
            new AgentRuntimeCoordinator(registry));
        using var coordinator = new AgentWorkspaceCoordinator(bridge, store, registry, factory, history);
        var sourceId = (await coordinator.CreateAsync(provider.Descriptor.Key, workspace.Path))!.Value;

        bridge.RaiseCommand("load_thread", value: "boom", workspaceId: sourceId);

        // The failure reports to the global History dock, never into the
        // source conversation (no resume_failed), and stays deliverable even
        // after the source workspace is gone (no workspaceId required).
        var failure = await bridge.WaitForEventAsync(
            "agent_thread_open_error",
            message => message.GetProperty("text").GetString()!.Contains("could not open"));
        Assert.AreEqual("boom", failure.GetProperty("threadId").GetString());
        Assert.Contains("Simulated thread store failure", failure.GetProperty("detail").GetString()!);
        Assert.AreEqual(0, bridge.Events.Count(message =>
            message.TryGetProperty("type", out var type) && type.GetString() == "resume_failed"));
    }

    [TestMethod]
    public async Task CloseAsync_DuringRestore_CompletesWithoutUnobservedFailure()
    {
        using var fixture = new Fixture(nameof(CloseAsync_DuringRestore_CompletesWithoutUnobservedFailure));
        var thread = CreateRestorableThread(fixture.Store, fixture.Workspace.Path, "eta");

        var openTask = fixture.Coordinator.OpenThreadAsync(thread.ThreadId);
        var workspaceId = await fixture.Bridge.WaitForEventAsync("workspace_activated")
            .ContinueWith(task => Guid.Parse(task.Result.GetProperty("workspaceId").GetString()!));

        await fixture.Coordinator.CloseAsync(workspaceId, WorkspaceCloseReason.User);
        var opened = await openTask;

        Assert.AreEqual(workspaceId, opened);
        Assert.IsEmpty(fixture.Coordinator.Workspaces);
    }
}
