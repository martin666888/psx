using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentProfileCommandTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static (AgentWorkspaceCoordinator coordinator, RecordingAgentBridgeService bridge, IDisposable scope)
        CreateCoordinator(string scope)
    {
        var workspace = TestWorkspace.Create(scope);
        var store = new AgentThreadStore(Path.Combine(workspace.Path, "store"));
        var bridge = new RecordingAgentBridgeService();
        var runtime = new CountingRuntime(workspace.Path);
        var registry = new AgentProviderRegistry(
            [new TestProvider("test", "Test Agent", runtime, [])],
            new AgentProviderOptions { DefaultProviderKey = "test" });
        var history = new AgentHistoryCatalog();
        var factory = new AgentWorkspaceFactory(
            bridge, new NullTabManagementService(), new NullTerminalBridgeService(),
            store, new NullAgentDirectoryPicker(), registry, new AgentRuntimeCoordinator(registry));
        var coordinator = new AgentWorkspaceCoordinator(bridge, store, registry, factory, history);
        return (coordinator, bridge, new CompositeDisposable(coordinator, history, workspace));
    }

    [TestMethod]
    public async Task GlobalProfileCommands_WithoutAgentWorkspace_LoadAndApplySavedName()
    {
        var (coordinator, bridge, scope) = CreateCoordinator(
            nameof(GlobalProfileCommands_WithoutAgentWorkspace_LoadAndApplySavedName));
        using (scope)
        {
            bridge.RaiseCommand(
                "profile_set_name", requestId: "global-profile-set", value: "Terminal User");

            var mutationReply = await bridge.WaitForEventAsync(
                "agent_profile",
                message => message.GetProperty("requestId").GetString() == "global-profile-set");
            Assert.AreEqual("Terminal User", mutationReply.GetProperty("displayName").GetString());
            Assert.AreEqual(1, mutationReply.GetProperty("revision").GetInt64());

            bridge.RaiseCommand("profile_get", requestId: "global-profile-get");

            var reply = await bridge.WaitForEventAsync(
                "agent_profile",
                message => message.GetProperty("requestId").GetString() == "global-profile-get");
            Assert.IsEmpty(coordinator.Workspaces);
            Assert.AreEqual("Terminal User", reply.GetProperty("displayName").GetString());
            Assert.AreEqual(1, reply.GetProperty("revision").GetInt64());
            Assert.IsFalse(reply.TryGetProperty("workspaceId", out _));
        }
    }

    [TestMethod]
    public async Task ProfileGet_EchoesRequestId()
    {
        var (coordinator, bridge, scope) = CreateCoordinator(nameof(ProfileGet_EchoesRequestId));
        using (scope)
        {
            var workspaceId = (await coordinator.CreateAsync("test"))!.Value;

            bridge.RaiseCommand("profile_get", requestId: "req-1", workspaceId: workspaceId);

            var reply = await bridge.WaitForEventAsync(
                "agent_profile", message => message.GetProperty("requestId").GetString() == "req-1");
            Assert.IsFalse(string.IsNullOrWhiteSpace(reply.GetProperty("displayName").GetString()));
            Assert.AreEqual(0, reply.GetProperty("revision").GetInt64());
        }
    }

    [TestMethod]
    public async Task ProfileSetName_RequesterEchoesRequestId_OthersBroadcastWithout()
    {
        var (coordinator, bridge, scope) = CreateCoordinator(
            nameof(ProfileSetName_RequesterEchoesRequestId_OthersBroadcastWithout));
        using (scope)
        {
            var first = (await coordinator.CreateAsync("test"))!.Value;
            var second = (await coordinator.CreateAsync("test"))!.Value;

            bridge.RaiseCommand("profile_set_name", requestId: "set-1", value: "Codey", workspaceId: first);

            // Requester reply carries the requestId and completes its pending request.
            var requesterReply = await bridge.WaitForEventAsync(
                "agent_profile",
                message => message.GetProperty("workspaceId").GetString() == first.ToString()
                    && message.TryGetProperty("requestId", out var id)
                    && id.GetString() == "set-1");
            Assert.AreEqual("Codey", requesterReply.GetProperty("displayName").GetString());

            // The other workspace receives a requestId-free broadcast.
            var broadcast = await bridge.WaitForEventAsync(
                "agent_profile",
                message => message.GetProperty("workspaceId").GetString() == second.ToString());
            Assert.AreEqual("Codey", broadcast.GetProperty("displayName").GetString());
            Assert.AreEqual(JsonValueKind.Null, broadcast.GetProperty("requestId").ValueKind);
            Assert.AreEqual(1, broadcast.GetProperty("revision").GetInt64());
        }
    }

    [TestMethod]
    public async Task ProfileSetAvatar_InvalidBase64_ReportsErrorToRequesterOnly()
    {
        var (coordinator, bridge, scope) = CreateCoordinator(
            nameof(ProfileSetAvatar_InvalidBase64_ReportsErrorToRequesterOnly));
        using (scope)
        {
            var workspaceId = (await coordinator.CreateAsync("test"))!.Value;

            bridge.RaiseCommand("profile_set_avatar", requestId: "av-1", value: "!!!not-base64!!!",
                workspaceId: workspaceId);

            var reply = await bridge.WaitForEventAsync(
                "agent_profile", message => message.GetProperty("requestId").GetString() == "av-1");
            Assert.AreEqual(JsonValueKind.String, reply.GetProperty("error").ValueKind);
        }
    }

    [TestMethod]
    public async Task ProfileSetAvatar_ValidPng_BroadcastsDataUrl()
    {
        var (coordinator, bridge, scope) = CreateCoordinator(
            nameof(ProfileSetAvatar_ValidPng_BroadcastsDataUrl));
        using (scope)
        {
            var workspaceId = (await coordinator.CreateAsync("test"))!.Value;

            bridge.RaiseCommand("profile_set_avatar", requestId: "av-2",
                value: Convert.ToBase64String(TinyPng), workspaceId: workspaceId);

            var reply = await bridge.WaitForEventAsync(
                "agent_profile", message => message.GetProperty("requestId").GetString() == "av-2");
            Assert.AreEqual(JsonValueKind.Null, reply.GetProperty("error").ValueKind);
            StringAssert.StartsWith(
                reply.GetProperty("avatarDataUrl").GetString()!, "data:image/png;base64,");
        }
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
