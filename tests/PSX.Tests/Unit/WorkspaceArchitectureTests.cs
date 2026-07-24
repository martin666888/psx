using Microsoft.Web.WebView2.Wpf;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentProviderRegistryTests
{
    [TestMethod]
    public void Constructor_MultipleProviders_ResolvesDefaultAndLegacyKeys()
    {
        using var workspace = TestWorkspace.Create(nameof(Constructor_MultipleProviders_ResolvesDefaultAndLegacyKeys));
        var runtime = new CountingRuntime(workspace.Path);
        var first = new TestProvider("first", "First", runtime, ["old-first"]);
        var second = new TestProvider("second", "Second", runtime, []);

        var registry = new AgentProviderRegistry(
            [first, second],
            new AgentProviderOptions { DefaultProviderKey = "second" });

        Assert.AreSame(second, registry.DefaultProvider);
        Assert.AreSame(first, registry.Find("old-first"));
        CollectionAssert.AreEqual(new[] { first, second }, registry.Providers.ToArray());
    }

    [TestMethod]
    public void Constructor_DuplicateProviderOrLegacyKey_FailsFast()
    {
        using var workspace = TestWorkspace.Create(nameof(Constructor_DuplicateProviderOrLegacyKey_FailsFast));
        var runtime = new CountingRuntime(workspace.Path);
        var first = new TestProvider("first", "First", runtime, ["shared"]);
        var second = new TestProvider("shared", "Second", runtime, []);

        Assert.ThrowsExactly<InvalidOperationException>(() => new AgentProviderRegistry(
            [first, second],
            new AgentProviderOptions { DefaultProviderKey = "first" }));
    }

    [TestMethod]
    public async Task RuntimeCoordinator_SharedRuntime_PreparesAndRefreshesOnce()
    {
        using var workspace = TestWorkspace.Create(nameof(RuntimeCoordinator_SharedRuntime_PreparesAndRefreshesOnce));
        var runtime = new CountingRuntime(workspace.Path);
        var first = new TestProvider("first", "First", runtime, []);
        var second = new TestProvider("second", "Second", runtime, []);
        var registry = new AgentProviderRegistry(
            [first, second],
            new AgentProviderOptions { DefaultProviderKey = "first" });
        using var coordinator = new AgentRuntimeCoordinator(registry);

        await coordinator.PrepareForStartupAsync();
        await coordinator.RefreshReadyAsync();

        Assert.AreEqual(1, runtime.PrepareCount);
        Assert.AreEqual(1, runtime.RefreshCount);
    }

    [TestMethod]
    public void EventSink_SendEvent_AlwaysAddsWorkspaceIdAndDropsAfterDispose()
    {
        var bridge = new RecordingAgentBridgeService();
        var workspaceId = Guid.NewGuid();
        using var sink = new AgentWorkspaceEventSink(workspaceId, bridge);

        sink.SendEventAsync(new { type = "agent_state", workspaceId = Guid.NewGuid() }).GetAwaiter().GetResult();
        sink.Dispose();
        sink.SendEventAsync(new { type = "late" }).GetAwaiter().GetResult();

        Assert.HasCount(1, bridge.Events);
        Assert.AreEqual(
            workspaceId.ToString(),
            bridge.Events.Single().GetProperty("workspaceId").GetString());
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class AgentWorkspaceCoordinatorTests
{
    [TestMethod]
    public async Task CreateAsync_SixthAgentIsRejectedWithoutAffectingTerminalContract()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateAsync_SixthAgentIsRejectedWithoutAffectingTerminalContract));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var runtime = new CountingRuntime(workspace.Path);
        var provider = new TestProvider("test", "Test Agent", runtime, []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var tabs = new NullTabManagementService();
        var terminalBridge = new NullTerminalBridgeService();
        var directoryPicker = new NullAgentDirectoryPicker();
        var factory = new AgentWorkspaceFactory(
            bridge, tabs, terminalBridge, store, directoryPicker, registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        for (var index = 0; index < AgentWorkspaceCoordinator.MaxAgentWorkspaces; index++)
            Assert.IsNotNull(await coordinator.CreateAsync("test", workspace.Path));

        Assert.IsNull(await coordinator.CreateAsync("test", workspace.Path));
        Assert.HasCount(AgentWorkspaceCoordinator.MaxAgentWorkspaces, coordinator.Workspaces);
        Assert.IsTrue(bridge.Events.Any(item =>
            item.GetProperty("type").GetString() == "agent_workspace_limit_reached"));
    }

    [TestMethod]
    public async Task OpenThreadAsync_AlreadyOpenThreadActivatesExistingWorkspace()
    {
        using var workspace = TestWorkspace.Create(nameof(OpenThreadAsync_AlreadyOpenThreadActivatesExistingWorkspace));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var thread = store.CreateThread(workspace.Path);
        thread.Provider = "test";
        thread.Messages.Add(new AgentMessage { Role = "user", Text = "hello" });
        store.SaveThread(thread);
        var bridge = new RecordingAgentBridgeService();
        var provider = new TestProvider("test", "Test Agent", new CountingRuntime(workspace.Path), []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var tabs = new NullTabManagementService();
        var terminalBridge = new NullTerminalBridgeService();
        var directoryPicker = new NullAgentDirectoryPicker();
        var factory = new AgentWorkspaceFactory(
            bridge, tabs, terminalBridge, store, directoryPicker, registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var first = await coordinator.OpenThreadAsync(thread.ThreadId);
        var second = await coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.IsNotNull(first);
        Assert.AreEqual(first, second);
        Assert.HasCount(1, coordinator.Workspaces);
    }

    [TestMethod]
    public async Task CloseAsync_EmptyDraftDeletesThreadAndDropsLateRouting()
    {
        using var workspace = TestWorkspace.Create(nameof(CloseAsync_EmptyDraftDeletesThreadAndDropsLateRouting));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var provider = new TestProvider("test", "Test Agent", new CountingRuntime(workspace.Path), []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var tabs = new NullTabManagementService();
        var terminalBridge = new NullTerminalBridgeService();
        var directoryPicker = new NullAgentDirectoryPicker();
        var factory = new AgentWorkspaceFactory(
            bridge, tabs, terminalBridge, store, directoryPicker, registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);
        var workspaceId = (await coordinator.CreateAsync("test", workspace.Path))!.Value;
        var threadId = coordinator.Workspaces.Single().ThreadId!;

        await coordinator.CloseAsync(workspaceId, WorkspaceCloseReason.User);
        bridge.Submit("late message", workspaceId);

        Assert.IsNull(store.LoadThread(threadId));
        Assert.IsEmpty(coordinator.Workspaces);
    }

    [TestMethod]
    public async Task CreateAsync_ConcurrentRequests_NeverExceedAgentLimit()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateAsync_ConcurrentRequests_NeverExceedAgentLimit));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var provider = new TestProvider("test", "Test Agent", new CountingRuntime(workspace.Path), []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => coordinator.CreateAsync("test", workspace.Path)));

        Assert.AreEqual(AgentWorkspaceCoordinator.MaxAgentWorkspaces, results.Count(id => id.HasValue));
        Assert.HasCount(AgentWorkspaceCoordinator.MaxAgentWorkspaces, coordinator.Workspaces);
    }

    [TestMethod]
    public async Task OpenThreadAsync_UnknownProvider_UsesTranscriptOnlyWithoutStartingDefaultRuntime()
    {
        using var workspace = TestWorkspace.Create(nameof(OpenThreadAsync_UnknownProvider_UsesTranscriptOnlyWithoutStartingDefaultRuntime));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var thread = store.CreateThread(workspace.Path);
        thread.Provider = "missing-provider";
        thread.Messages.Add(new AgentMessage { Role = "user", Text = "saved transcript" });
        store.SaveThread(thread);
        var bridge = new RecordingAgentBridgeService();
        var runtime = new CountingRuntime(workspace.Path);
        var provider = new TestProvider("default", "Default Agent", runtime, []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "default" });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var workspaceId = await coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.IsNotNull(workspaceId);
        Assert.AreEqual(AgentWorkspaceState.TranscriptOnly, coordinator.Workspaces.Single().AgentState);
        Assert.AreEqual(0, runtime.ProcessSpecCount);
        Assert.IsFalse(bridge.Events.Any(message =>
            message.GetProperty("type").GetString() == "runtime_status"
            && message.TryGetProperty("workspaceId", out var eventWorkspaceId)
            && eventWorkspaceId.GetString() == workspaceId.Value.ToString()));
        Assert.IsTrue(bridge.Events.Any(message =>
            message.GetProperty("type").GetString() == "agent_state"
            && message.GetProperty("workspaceId").GetString() == workspaceId.Value.ToString()
            && message.GetProperty("status").GetString() == "transcript_only"));
    }

    [TestMethod]
    public async Task PublishStateAsync_TwoProviders_EmitsDataDrivenCatalog()
    {
        using var workspace = TestWorkspace.Create(nameof(PublishStateAsync_TwoProviders_EmitsDataDrivenCatalog));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var runtime = new CountingRuntime(workspace.Path);
        var first = new TestProvider("first", "First Agent", runtime, []);
        var second = new TestProvider("second", "Second Agent", runtime, []);
        var registry = new AgentProviderRegistry(
            [first, second],
            new AgentProviderOptions { DefaultProviderKey = "second" });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        await coordinator.PublishStateAsync();

        var catalog = bridge.Events.Single(message => message.GetProperty("type").GetString() == "agent_providers");
        var providers = catalog.GetProperty("providers").EnumerateArray().ToArray();
        Assert.HasCount(2, providers);
        Assert.AreEqual("first", providers[0].GetProperty("key").GetString());
        Assert.IsFalse(providers[0].GetProperty("isDefault").GetBoolean());
        Assert.AreEqual("second", providers[1].GetProperty("key").GetString());
        Assert.IsTrue(providers[1].GetProperty("isDefault").GetBoolean());
    }

    [TestMethod]
    public async Task ShutdownAsync_FiveWorkspaces_BeginsAllCancellationsInParallel()
    {
        using var workspace = TestWorkspace.Create(nameof(ShutdownAsync_FiveWorkspaces_BeginsAllCancellationsInParallel));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var provider = new TestProvider("test", "Test Agent", new CountingRuntime(workspace.Path), []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var factory = new BarrierWorkspaceFactory(bridge, AgentWorkspaceCoordinator.MaxAgentWorkspaces);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        for (var index = 0; index < AgentWorkspaceCoordinator.MaxAgentWorkspaces; index++)
            Assert.IsNotNull(await coordinator.CreateAsync("test", workspace.Path));

        var shutdown = coordinator.ShutdownAsync();
        await factory.AllCancellationsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.ReleaseCancellations.TrySetResult();
        await shutdown;

        Assert.IsEmpty(coordinator.Workspaces);
    }

    [TestMethod]
    public async Task RuntimeStatus_OnlyPublishesForActiveWorkspaceAndRestoresCachedMessage()
    {
        using var workspace = TestWorkspace.Create(nameof(RuntimeStatus_OnlyPublishesForActiveWorkspaceAndRestoresCachedMessage));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var firstRuntime = new CountingRuntime(workspace.Path);
        var secondRuntime = new CountingRuntime(workspace.Path);
        var firstProvider = new TestProvider("first", "First Agent", firstRuntime, []);
        var secondProvider = new TestProvider("second", "Second Agent", secondRuntime, []);
        var registry = new AgentProviderRegistry(
            [firstProvider, secondProvider],
            new AgentProviderOptions { DefaultProviderKey = "first" });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);
        var statuses = new List<ActiveRuntimeStatusChangedEventArgs>();
        coordinator.ActiveRuntimeStatusChanged += (_, args) => statuses.Add(args);

        var firstWorkspace = (await coordinator.CreateAsync("first", workspace.Path))!.Value;
        var secondWorkspace = (await coordinator.CreateAsync("second", workspace.Path))!.Value;
        statuses.Clear();

        await coordinator.ActivateAsync(firstWorkspace);
        firstRuntime.PublishStatus("ACP 1.0 · checking updates");
        secondRuntime.PublishStatus("ACP 2.0 · ready");

        Assert.AreEqual(firstWorkspace, statuses.Last().WorkspaceId);
        Assert.AreEqual("First Agent", statuses.Last().ProviderDisplayName);
        Assert.AreEqual("ACP 1.0 · checking updates", statuses.Last().Message);

        await coordinator.ActivateAsync(secondWorkspace);

        Assert.AreEqual(secondWorkspace, statuses.Last().WorkspaceId);
        Assert.AreEqual("Second Agent", statuses.Last().ProviderDisplayName);
        Assert.AreEqual("ACP 2.0 · ready", statuses.Last().Message);

        var statusCountBeforeInactiveUpdate = statuses.Count;
        firstRuntime.PublishStatus("ACP 1.1 · updated");

        Assert.HasCount(statusCountBeforeInactiveUpdate, statuses);
    }

    [TestMethod]
    public async Task DeactivateRuntimeStatus_ClearsActiveStatusAndSuppressesLaterRuntimeUpdates()
    {
        using var workspace = TestWorkspace.Create(nameof(DeactivateRuntimeStatus_ClearsActiveStatusAndSuppressesLaterRuntimeUpdates));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var runtime = new CountingRuntime(workspace.Path);
        var provider = new TestProvider("test", "Test Agent", runtime, []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);
        var statuses = new List<ActiveRuntimeStatusChangedEventArgs>();
        coordinator.ActiveRuntimeStatusChanged += (_, args) => statuses.Add(args);

        _ = await coordinator.CreateAsync("test", workspace.Path);
        runtime.PublishStatus("ACP 1.0 · ready");
        coordinator.DeactivateRuntimeStatus();
        var statusCountAfterDeactivation = statuses.Count;
        runtime.PublishStatus("ACP 1.1 · checking updates");

        Assert.IsNull(statuses.Last().WorkspaceId);
        Assert.IsNull(statuses.Last().Message);
        Assert.HasCount(statusCountAfterDeactivation, statuses);
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class WorkspaceManagerTests
{
    [TestMethod]
    public async Task CloseAsync_LastWorkspace_CreatesDefaultTerminalUnlessShuttingDown()
    {
        var terminals = new RecordingTabManagementService();
        using var agents = new StubAgentWorkspaceCoordinator();
        var bridge = new RecordingAgentBridgeService();
        using var manager = new WorkspaceManager(terminals, agents, bridge);
        var first = (await manager.CreateTerminalAsync())!.Value;

        await manager.CloseAsync(first);

        Assert.HasCount(1, manager.Workspaces);
        Assert.AreEqual(2, terminals.CreateCount);

        manager.BeginShutdown();
        await manager.CloseAsync(manager.Workspaces.Single().WorkspaceId);

        Assert.IsEmpty(manager.Workspaces);
        Assert.AreEqual(2, terminals.CreateCount);
    }

    [TestMethod]
    public async Task ActivateAsync_TerminalClearsAgentRuntimeStatus()
    {
        var terminals = new RecordingTabManagementService();
        using var agents = new StubAgentWorkspaceCoordinator();
        var bridge = new RecordingAgentBridgeService();
        using var manager = new WorkspaceManager(terminals, agents, bridge);

        _ = await manager.CreateTerminalAsync();

        Assert.AreEqual(1, agents.DeactivateRuntimeStatusCount);
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class AgentHistoryCatalogTests
{
    [TestMethod]
    public async Task Invalidate_BurstOfUpdates_RaisesOneTrailingNotification()
    {
        using var catalog = new AgentHistoryCatalog();
        var count = 0;
        catalog.Invalidated += (_, _) => Interlocked.Increment(ref count);

        for (var index = 0; index < 20; index++)
            catalog.Invalidate();

        await Task.Delay(400);

        Assert.AreEqual(1, Volatile.Read(ref count));
    }
}

internal sealed class TestProvider(
    string key,
    string displayName,
    IAcpAgentRuntime runtime,
    IReadOnlyCollection<string> legacyKeys) : IAcpAgentProvider
{
    public AgentDescriptor Descriptor { get; } = new(key, displayName, displayName, legacyKeys);
    public IAcpAgentRuntime Runtime { get; } = runtime;
    public AcpClientCapabilityProfile ClientCapabilities { get; } = new()
    {
        FileSystemReadText = true,
        FileSystemWriteText = true,
        Terminal = true,
        SessionBooleanConfig = true,
        ElicitationFormUrl = true,
        TerminalOutputMeta = true
    };
    public object CreateNewSessionParameters(string workingDirectory) => new { cwd = workingDirectory };
    public object CreateLoadSessionParameters(string sessionId, string workingDirectory) =>
        new { sessionId, cwd = workingDirectory };
    public bool IsCommandVisible(string normalizedCommand) => true;
    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId) => null;
}

internal sealed class CountingRuntime(string root) : IAcpAgentRuntime
{
    public event Action<string>? StatusChanged;
    public int PrepareCount { get; private set; }
    public int RefreshCount { get; private set; }
    public int ProcessSpecCount { get; private set; }
    public string LogPath => Path.Combine(root, "runtime.log");
    public bool IsReady() => true;
    public string BuildStatusText(string? suffix = null) => suffix ?? "Ready";
    public Task<AcpRuntimeOperationResult> EnsureInstalledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady, "Ready"));
    public Task<AcpRuntimeOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return Task.FromResult(new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady, "Ready"));
    }
    public Task PrepareForStartupAsync(CancellationToken cancellationToken = default)
    {
        PrepareCount++;
        return Task.CompletedTask;
    }
    public AcpProcessSpec CreateProcessSpec(string workingDirectory)
    {
        ProcessSpecCount++;
        return new AcpProcessSpec
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            WorkingDirectory = workingDirectory
        };
    }
    public void Dispose() { }
    public void PublishStatus(string status) => StatusChanged?.Invoke(status);
}

internal sealed class RecordingTabManagementService : ITabManagementService
{
    public int CreateCount { get; private set; }
    public event EventHandler<TabCreatedEventArgs>? TabCreated;
    public event EventHandler<TabClosedEventArgs>? TabClosed;
    public event EventHandler<TabTitleChangedEventArgs>? TabTitleChanged { add { } remove { } }

    public Task<Guid> CreateTabAsync(ShellProfile? profile = null)
    {
        CreateCount++;
        var id = Guid.NewGuid();
        TabCreated?.Invoke(this, new TabCreatedEventArgs { SessionId = id, Title = profile?.Name ?? "Terminal" });
        return Task.FromResult(id);
    }

    public Task CloseTabAsync(Guid sessionId)
    {
        TabClosed?.Invoke(this, new TabClosedEventArgs { SessionId = sessionId });
        return Task.CompletedTask;
    }

    public Task SwitchTabAsync(Guid sessionId) => Task.CompletedTask;
    public Task ResizeTabAsync(Guid sessionId, int cols, int rows) => Task.CompletedTask;
    public TerminalSession? GetSession(Guid sessionId) => null;
}

internal sealed class StubAgentWorkspaceCoordinator : IAgentWorkspaceCoordinator
{
    public int DeactivateRuntimeStatusCount { get; private set; }
    public IReadOnlyList<WorkspaceDescriptor> Workspaces => Array.Empty<WorkspaceDescriptor>();
    public IReadOnlyList<AgentProviderCatalogItem> ProviderCatalog => Array.Empty<AgentProviderCatalogItem>();
    public event EventHandler<AgentWorkspaceEventArgs>? WorkspaceCreated { add { } remove { } }
    public event EventHandler<AgentWorkspaceEventArgs>? WorkspaceChanged { add { } remove { } }
    public event EventHandler<AgentWorkspaceClosedEventArgs>? WorkspaceClosed { add { } remove { } }
    public event EventHandler<Guid>? WorkspaceActivationRequested { add { } remove { } }
    public event EventHandler<ActiveRuntimeStatusChangedEventArgs>? ActiveRuntimeStatusChanged { add { } remove { } }
    public Task<Guid?> CreateAsync(string providerKey, string? workingDirectory = null) => Task.FromResult<Guid?>(null);
    public Task<Guid?> OpenThreadAsync(string threadId) => Task.FromResult<Guid?>(null);
    public Task ActivateAsync(Guid workspaceId) => Task.CompletedTask;
    public void DeactivateRuntimeStatus() => DeactivateRuntimeStatusCount++;
    public Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason) => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
    public Guid? FindOpenThread(string threadId) => null;
    public Task PublishStateAsync() => Task.CompletedTask;
    public void Dispose() { }
}

internal sealed class BarrierWorkspaceFactory(
    IAgentBridgeService bridge,
    int expectedCancellations) : IAgentWorkspaceFactory
{
    private int _startedCancellations;

    public TaskCompletionSource AllCancellationsStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCancellations { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentWorkspaceSessionHandle Create(
        Guid workspaceId,
        IAcpAgentProvider? provider,
        AgentThread thread,
        Func<System.Text.Json.Nodes.JsonObject, bool> beforeEvent)
    {
        var eventSink = new AgentWorkspaceEventSink(workspaceId, bridge, beforeEvent);
        return new AgentWorkspaceSessionHandle
        {
            EventSink = eventSink,
            Session = new BarrierWorkspaceSession(
                workspaceId,
                provider?.Descriptor.Key ?? thread.Provider,
                thread,
                OnCancellationStarted,
                ReleaseCancellations.Task)
        };
    }

    private void OnCancellationStarted()
    {
        if (Interlocked.Increment(ref _startedCancellations) == expectedCancellations)
            AllCancellationsStarted.TrySetResult();
    }
}

internal sealed class BarrierWorkspaceSession(
    Guid workspaceId,
    string providerKey,
    AgentThread thread,
    Action cancellationStarted,
    Task cancellationRelease) : IAgentWorkspaceSession
{
    public Guid WorkspaceId { get; } = workspaceId;
    public string ThreadId => thread.ThreadId;
    public string ProviderKey { get; } = providerKey;
    public string WorkingDirectory => thread.Cwd;
    public bool IsDraft => true;
    public Task SubmitMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
    public async Task CancelAsync()
    {
        cancellationStarted();
        await cancellationRelease.ConfigureAwait(false);
    }
    public Task ChangeDirectoryAsync(string path) => Task.CompletedTask;
    public Task ListThreadsAsync() => Task.CompletedTask;
    public Task PublishStateAsync() => Task.CompletedTask;
    public Task RestoreAsync() => Task.CompletedTask;
    public void Dispose() { }
}
