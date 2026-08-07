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
    public async Task RuntimeCoordinator_Startup_PreparesWithoutTouchingNpm()
    {
        using var workspace = TestWorkspace.Create(nameof(RuntimeCoordinator_Startup_PreparesWithoutTouchingNpm));
        var runtime = new CountingRuntime(workspace.Path);
        var first = new TestProvider("first", "First", runtime, []);
        var second = new TestProvider("second", "Second", runtime, []);
        var registry = new AgentProviderRegistry(
            [first, second],
            new AgentProviderOptions { DefaultProviderKey = "first" });
        using var coordinator = new AgentRuntimeCoordinator(registry);

        await coordinator.PrepareForStartupAsync();

        // Startup promotes staged directories locally and nothing else:
        // updates are strictly user-triggered, so npm is never reached here.
        Assert.AreEqual(1, runtime.PrepareCount);
        Assert.AreEqual(0, runtime.RefreshCount);
    }

    [TestMethod]
    public async Task RuntimeCoordinator_PendingUpdateStaged_ShortCircuitsWithoutRefreshing()
    {
        using var workspace = TestWorkspace.Create(nameof(RuntimeCoordinator_PendingUpdateStaged_ShortCircuitsWithoutRefreshing));
        var runtime = new CountingRuntime(workspace.Path) { HasPendingUpdate = true };
        var registry = new AgentProviderRegistry(
            [new TestProvider("first", "First", runtime, [])],
            new AgentProviderOptions { DefaultProviderKey = "first" });
        using var coordinator = new AgentRuntimeCoordinator(registry);
        var states = new List<string>();
        coordinator.UpdateStatusChanged += (_, args) => states.Add(args.State);

        var result = await coordinator.RequestUpdateAsync(runtime);

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
        Assert.AreEqual(0, runtime.RefreshCount, "a staged update must never trigger another npm run");
        CollectionAssert.AreEqual(new[] { "staged_restart_required" }, states);
        Assert.AreEqual("staged_restart_required", coordinator.GetUpdateSnapshot(runtime)?.State);
    }

    [TestMethod]
    public async Task RuntimeCoordinator_KeepsOutcomeSnapshotForLateWorkspaces()
    {
        using var workspace = TestWorkspace.Create(nameof(RuntimeCoordinator_KeepsOutcomeSnapshotForLateWorkspaces));
        var runtime = new CountingRuntime(workspace.Path)
        {
            RefreshResult = new AcpRuntimeOperationResult(AcpRuntimeOperationKind.Failed, "npm exploded")
        };
        var registry = new AgentProviderRegistry(
            [new TestProvider("first", "First", runtime, [])],
            new AgentProviderOptions { DefaultProviderKey = "first" });
        using var coordinator = new AgentRuntimeCoordinator(registry);

        Assert.IsNull(coordinator.GetUpdateSnapshot(runtime));
        await coordinator.RequestUpdateAsync(runtime);

        // A workspace created after the run reads the process-wide outcome
        // instead of defaulting back to idle.
        var snapshot = coordinator.GetUpdateSnapshot(runtime);
        Assert.AreEqual("failed", snapshot?.State);
        Assert.AreEqual("npm exploded", snapshot?.Message);
        Assert.IsFalse(coordinator.IsUpdateInFlight(runtime), "the in-flight entry must be released on failure");
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
    public async Task CreateAndRestore_PropagateProviderIconKeyToWorkspace()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateAndRestore_PropagateProviderIconKeyToWorkspace));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var provider = new TestProvider(
            "branded",
            "Branded Agent",
            new CountingRuntime(workspace.Path),
            [],
            "brand-icon");
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "branded" });
        using var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            store,
            new NullAgentDirectoryPicker(),
            registry,
            new AgentRuntimeCoordinator(registry));
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var createdWorkspaceId = await coordinator.CreateAsync("branded", workspace.Path);

        Assert.IsNotNull(createdWorkspaceId);
        Assert.AreEqual(
            "brand-icon",
            coordinator.Workspaces.Single(item => item.WorkspaceId == createdWorkspaceId).IconKey);

        var restoredThread = store.CreateThread(workspace.Path);
        restoredThread.Provider = "branded";
        restoredThread.Messages.Add(new AgentMessage { Role = "user", Text = "saved" });
        store.SaveThread(restoredThread);

        var restoredWorkspaceId = await coordinator.OpenThreadAsync(restoredThread.ThreadId);

        Assert.IsNotNull(restoredWorkspaceId);
        Assert.AreEqual(
            "brand-icon",
            coordinator.Workspaces.Single(item => item.WorkspaceId == restoredWorkspaceId).IconKey);
    }

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
            bridge, tabs, terminalBridge, store, directoryPicker, registry,
            new AgentRuntimeCoordinator(registry));
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
            bridge, tabs, terminalBridge, store, directoryPicker, registry,
            new AgentRuntimeCoordinator(registry));
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
            bridge, tabs, terminalBridge, store, directoryPicker, registry,
            new AgentRuntimeCoordinator(registry));
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
            registry,
            new AgentRuntimeCoordinator(registry));
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
            registry,
            new AgentRuntimeCoordinator(registry));
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var workspaceId = await coordinator.OpenThreadAsync(thread.ThreadId);

        Assert.IsNotNull(workspaceId);
        Assert.AreEqual(AgentWorkspaceState.TranscriptOnly, coordinator.Workspaces.Single().AgentState);
        Assert.AreEqual("agent", coordinator.Workspaces.Single().IconKey);
        Assert.AreEqual(0, runtime.ProcessSpecCount);
        Assert.IsFalse(bridge.Events.Any(message =>
            message.GetProperty("type").GetString() == "runtime_status"
            && message.TryGetProperty("workspaceId", out var eventWorkspaceId)
            && eventWorkspaceId.GetString() == workspaceId.Value.ToString()));
        Assert.IsTrue(bridge.Events.Any(message =>
            message.GetProperty("type").GetString() == "agent_state"
            && message.GetProperty("workspaceId").GetString() == workspaceId.Value.ToString()
            && message.GetProperty("status").GetString() == "transcript_only"));
        // The toolbar Update button must be disabled for saved transcripts.
        Assert.IsTrue(bridge.Events.Any(message =>
            message.GetProperty("type").GetString() == "runtime_update_status"
            && message.GetProperty("workspaceId").GetString() == workspaceId.Value.ToString()
            && message.GetProperty("state").GetString() == "unavailable"));
    }

    [TestMethod]
    public async Task PublishStateAsync_TwoProviders_EmitsDataDrivenCatalog()
    {
        using var workspace = TestWorkspace.Create(nameof(PublishStateAsync_TwoProviders_EmitsDataDrivenCatalog));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var runtime = new CountingRuntime(workspace.Path);
        var first = new TestProvider("first", "First Agent", runtime, []);
        var second = new TestProvider("second", "Second Agent", runtime, [], iconKey: "kimi");
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
            registry,
            new AgentRuntimeCoordinator(registry));
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
        // Brand icons ride the catalog: descriptor IconKey when set, the
        // generic "agent" fallback otherwise. The frontend never branches on
        // provider names.
        Assert.AreEqual("agent", providers[0].GetProperty("iconKey").GetString());
        Assert.AreEqual("kimi", providers[1].GetProperty("iconKey").GetString());
    }

    [TestMethod]
    public async Task PendingPermissionEvents_ConcurrentUpdates_KeepWorkspaceStateConsistent()
    {
        using var workspace = TestWorkspace.Create(nameof(PendingPermissionEvents_ConcurrentUpdates_KeepWorkspaceStateConsistent));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var provider = new TestProvider("test", "Test Agent", new CountingRuntime(workspace.Path), []);
        var registry = new AgentProviderRegistry(
            [provider],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        using var history = new AgentHistoryCatalog();
        var factory = new BarrierWorkspaceFactory(bridge, expectedCancellations: 1);
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);
        var workspaceId = (await coordinator.CreateAsync("test", workspace.Path))!.Value;
        var eventSink = factory.EventSinks[workspaceId];

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            eventSink.SendEventAsync(new { type = "permission_request", requestId = $"p{index}" }))));
        await Task.WhenAll(Enumerable.Range(0, 99).Select(index => Task.Run(() =>
            eventSink.SendEventAsync(new { type = "permission_resolved", requestId = $"p{index}" }))));

        Assert.AreEqual(AgentWorkspaceState.WaitingForPermission, coordinator.Workspaces.Single().AgentState);

        await eventSink.SendEventAsync(new { type = "permission_resolved", requestId = "p99" });

        Assert.AreEqual(AgentWorkspaceState.Running, coordinator.Workspaces.Single().AgentState);
        factory.ReleaseCancellations.TrySetResult();
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
        using var manager = new WorkspaceManager(terminals, agents, bridge, new WorkspaceLayoutService());
        var first = (await manager.CreateTerminalAsync())!.Value;

        Assert.AreEqual("terminal", manager.Workspaces.Single().IconKey);

        await manager.CloseAsync(first);

        Assert.HasCount(1, manager.Workspaces);
        Assert.AreEqual(2, terminals.CreateCount);

        manager.BeginShutdown();
        await manager.CloseAsync(manager.Workspaces.Single().WorkspaceId);

        Assert.IsEmpty(manager.Workspaces);
        Assert.AreEqual(2, terminals.CreateCount);
    }

    [TestMethod]
    public async Task ActivateAsync_TerminalDoesNotTouchAgentRuntimeStatus()
    {
        var terminals = new RecordingTabManagementService();
        using var agents = new StubAgentWorkspaceCoordinator();
        var bridge = new RecordingAgentBridgeService();
        using var manager = new WorkspaceManager(terminals, agents, bridge, new WorkspaceLayoutService());

        _ = await manager.CreateTerminalAsync();

        // The bottom runtime status bar is retired, so activating a Terminal
        // workspace no longer clears any Agent runtime projection.
        Assert.AreEqual(1, terminals.CreateCount);
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
    IReadOnlyCollection<string> legacyKeys,
    string iconKey = "agent",
    IAgentUsageSource? usageSource = null,
    IAgentConfigSource? configSource = null) : IAcpAgentProvider
{
    public AgentDescriptor Descriptor { get; } = new(key, displayName, displayName, legacyKeys)
    {
        IconKey = iconKey
    };
    public IAcpAgentRuntime Runtime { get; } = runtime;
    public IAgentUsageSource? UsageSource { get; } = usageSource;
    public IAgentConfigSource? ConfigSource { get; } = configSource;
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
    public object CreateRestoreSessionParameters(string sessionId, string workingDirectory) =>
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
    public bool HasPendingUpdate { get; set; }
    public AcpRuntimeOperationResult RefreshResult { get; set; } =
        new(AcpRuntimeOperationKind.AlreadyReady, "Ready");
    public string LogPath => Path.Combine(root, "runtime.log");
    public bool SupportsSelfUpdate => true;
    public bool IsReady() => true;
    public string BuildStatusText(string? suffix = null) => suffix ?? "Ready";
    public RuntimeVersionSnapshot GetVersionSnapshot() =>
        new(CurrentVersion: "1.0.0", PendingVersion: HasPendingUpdate ? "1.1.0" : null, HasPendingUpdate: HasPendingUpdate);
    public Task<AcpRuntimeOperationResult> EnsureInstalledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AcpRuntimeOperationResult(AcpRuntimeOperationKind.AlreadyReady, "Ready"));
    public Task<AcpRuntimeOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return Task.FromResult(RefreshResult);
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
    public event EventHandler<string>? PaneFocusRequested { add { } remove { } }
    public event EventHandler<PaneRatioEventArgs>? PaneRatioRequested { add { } remove { } }
    public event EventHandler<PaneMoveEventArgs>? PaneMoveRequested { add { } remove { } }

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
    public Task ShutdownAsync(TimeSpan? timeout = null) => Task.CompletedTask;
}

internal sealed class StubAgentWorkspaceCoordinator : IAgentWorkspaceCoordinator
{
    public IReadOnlyList<WorkspaceDescriptor> Workspaces => Array.Empty<WorkspaceDescriptor>();
    public IReadOnlyList<AgentProviderCatalogItem> ProviderCatalog => Array.Empty<AgentProviderCatalogItem>();
    public event EventHandler<AgentWorkspaceEventArgs>? WorkspaceCreated { add { } remove { } }
    public event EventHandler<AgentWorkspaceEventArgs>? WorkspaceChanged { add { } remove { } }
    public event EventHandler<AgentWorkspaceClosedEventArgs>? WorkspaceClosed { add { } remove { } }
    public event EventHandler<Guid>? WorkspaceActivationRequested { add { } remove { } }
    public Task<Guid?> CreateAsync(string providerKey, string? workingDirectory = null) => Task.FromResult<Guid?>(null);
    public Task<Guid?> OpenThreadAsync(string threadId) => Task.FromResult<Guid?>(null);
    public Task ActivateAsync(Guid workspaceId) => Task.CompletedTask;
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

    public System.Collections.Concurrent.ConcurrentDictionary<Guid, AgentWorkspaceEventSink> EventSinks { get; } = new();
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
        EventSinks[workspaceId] = eventSink;
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
    public async Task CancelAsync()
    {
        cancellationStarted();
        await cancellationRelease.ConfigureAwait(false);
    }
    public Task ChangeDirectoryAsync(string path) => Task.CompletedTask;
    public Task ListThreadsAsync(string? requestId = null) => Task.CompletedTask;
    public Task PublishStateAsync() => Task.CompletedTask;
    public Task RestoreAsync() => Task.CompletedTask;
    public void Dispose() { }
}
