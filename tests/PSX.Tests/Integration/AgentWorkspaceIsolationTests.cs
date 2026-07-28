using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class AgentWorkspaceIsolationTests
{
    [TestMethod]
    public async Task TwoSessions_InterleavedPermissionAndOutput_RemainWorkspaceScoped()
    {
        using var workspace = TestWorkspace.Create(nameof(TwoSessions_InterleavedPermissionAndOutput_RemainWorkspaceScoped));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
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
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);

        var first = (await coordinator.CreateAsync(provider.Descriptor.Key, workspace.Path))!.Value;
        var second = (await coordinator.CreateAsync(provider.Descriptor.Key, workspace.Path))!.Value;

        bridge.Submit("permission", first);
        bridge.Submit("second workspace", second);

        var permission = await bridge.WaitForEventAsync(
            "permission_request",
            message => message.GetProperty("workspaceId").GetString() == first.ToString());
        await bridge.WaitForEventAsync(
            "run_finished",
            message => message.GetProperty("workspaceId").GetString() == second.ToString());

        var requestId = permission.GetProperty("requestId").GetString();
        bridge.RaiseCommand("agent_permission_response", requestId, "approve", first);
        await bridge.WaitForEventAsync(
            "run_finished",
            message => message.GetProperty("workspaceId").GetString() == first.ToString());

        var scopedEvents = bridge.Events.Where(message =>
            message.TryGetProperty("workspaceId", out var workspaceId)
            && (workspaceId.GetString() == first.ToString() || workspaceId.GetString() == second.ToString()))
            .ToArray();
        Assert.IsTrue(scopedEvents.Any(message =>
            message.GetProperty("type").GetString() == "assistant_delta"
            && message.GetProperty("workspaceId").GetString() == first.ToString()));
        Assert.IsTrue(scopedEvents.Any(message =>
            message.GetProperty("type").GetString() == "assistant_delta"
            && message.GetProperty("workspaceId").GetString() == second.ToString()));
        Assert.IsFalse(bridge.Events.Any(message =>
            message.GetProperty("type").GetString() == "permission_request"
            && message.GetProperty("workspaceId").GetString() == second.ToString()));

        var descriptors = coordinator.Workspaces.ToDictionary(item => item.WorkspaceId);
        Assert.AreEqual("permission", descriptors[first].Title);
        Assert.AreEqual("second workspace", descriptors[second].Title);
    }

    [TestMethod]
    public async Task CloseAsync_RunningWorkspace_PersistsPartialThinkingBeforeProcessShutdown()
    {
        using var workspace = TestWorkspace.Create(nameof(CloseAsync_RunningWorkspace_PersistsPartialThinkingBeforeProcessShutdown));
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
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
        using var coordinator = new AgentWorkspaceCoordinator(
            bridge, store, registry, factory, history);
        var workspaceId = (await coordinator.CreateAsync(provider.Descriptor.Key, workspace.Path))!.Value;
        var threadId = coordinator.Workspaces.Single().ThreadId!;

        bridge.Submit("hang", workspaceId);
        await bridge.WaitForEventAsync(
            "thinking_delta",
            message => message.GetProperty("workspaceId").GetString() == workspaceId.ToString());

        await coordinator.CloseAsync(workspaceId, WorkspaceCloseReason.User);

        var saved = store.LoadThread(threadId);
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.Messages.Any(message =>
            message.Role == "thinking"
            && message.Text.Contains("Waiting for cancellation", StringComparison.Ordinal)));
        Assert.IsEmpty(coordinator.Workspaces);
    }
}
