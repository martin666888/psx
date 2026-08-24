using System.Net;
using System.Net.Http;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiWebWorkspaceCoordinatorTests
{
    // ---- export URL / response validation ---------------------------------

    [TestMethod]
    public void IsAllowedExportUri_AcceptsCurrentOriginExportPathOnly()
    {
        const string origin = "http://127.0.0.1:4321";
        Assert.IsTrue(KimiWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:4321/api/v1/sessions/s1/export?x=1"), origin));
        Assert.IsFalse(KimiWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:9/api/v1/sessions/s1/export"), origin),
            "a stale kimi origin is refused");
        Assert.IsFalse(KimiWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:4321/api/v1/sessions/s1/meta"), origin),
            "only the exact export path is allowed");
        Assert.IsFalse(KimiWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("https://127.0.0.1:4321/api/v1/sessions/s1/export"), origin));
        Assert.IsFalse(KimiWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://user:pass@127.0.0.1:4321/api/v1/sessions/s1/export"), origin));
        Assert.IsFalse(KimiWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:4321/api/v1/sessions/../s1/export"), origin));
    }

    [TestMethod]
    public void ShouldRejectExportResponse_RejectsRedirectAndCrossOriginFinalUri()
    {
        const string origin = "http://127.0.0.1:4321";
        using var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Post, "http://127.0.0.1:4321/api/v1/sessions/s1/export"),
            Headers = { Location = new Uri("http://evil.example/steal.zip") }
        };
        Assert.IsTrue(KimiWebWorkspaceCoordinator.ShouldRejectExportResponse(redirect, origin),
            "a 3xx export response is refused (auto-follow is disabled)");

        using var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Post, "http://127.0.0.1:4321/api/v1/sessions/s1/export")
        };
        Assert.IsFalse(KimiWebWorkspaceCoordinator.ShouldRejectExportResponse(ok, origin));

        using var hopped = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Post, "http://127.0.0.1:9/api/v1/sessions/s1/export")
        };
        Assert.IsTrue(KimiWebWorkspaceCoordinator.ShouldRejectExportResponse(hopped, origin),
            "a final URI off the current kimi origin is refused");

        using var failure = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Post, "http://127.0.0.1:4321/api/v1/sessions/s1/export")
        };
        Assert.IsTrue(KimiWebWorkspaceCoordinator.ShouldRejectExportResponse(failure, origin));
    }

    [TestMethod]
    public void LooksLikeZip_RejectsNonZipMagic()
    {
        Assert.IsTrue(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
        Assert.IsTrue(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50, 0x4B, 0x05, 0x06 }));
        Assert.IsFalse(DshWebWorkspaceCoordinator.LooksLikeZip("not-a-zip"u8.ToArray()));
        Assert.IsFalse(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50 }));
    }

    [TestMethod]
    public async Task SaveExportAtomicallyAsync_OverSizeLimit_ThrowsAndKeepsExistingTarget()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveExportAtomicallyAsync_OverSizeLimit_ThrowsAndKeepsExistingTarget));
        var target = Path.Combine(workspace.Path, "kimi-export.zip");
        File.WriteAllText(target, "previous export");
        var source = new MemoryStream(new byte[4096]);

        await Assert.ThrowsAsync<DshWebWorkspaceCoordinator.DshExportTooLargeException>(() =>
            DshWebWorkspaceCoordinator.SaveExportAtomicallyAsync(
                source, new byte[] { 0x50, 0x4B, 0x03, 0x04 }, target, maximumBytes: 1024));

        Assert.AreEqual("previous export", File.ReadAllText(target));
        Assert.IsEmpty(LeftoverScratchFiles(workspace.Path, target));
    }

    [TestMethod]
    public async Task SaveExportAtomicallyAsync_ReplacesTargetOnlyAfterFullWrite()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveExportAtomicallyAsync_ReplacesTargetOnlyAfterFullWrite));
        var target = Path.Combine(workspace.Path, "kimi-export.zip");
        File.WriteAllText(target, "previous export");
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };

        await DshWebWorkspaceCoordinator.SaveExportAtomicallyAsync(
            new MemoryStream(payload[4..]), payload[..4], target,
            DshWebWorkspaceCoordinator.MaximumExportBytes);

        CollectionAssert.AreEqual(payload, File.ReadAllBytes(target));
        Assert.IsEmpty(LeftoverScratchFiles(workspace.Path, target));
    }

    // ---- coordinator lifecycle --------------------------------------------

    [TestMethod]
    public async Task CreateThenClose_RemovesDescriptorWithoutStoppingRuntime()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateThenClose_RemovesDescriptorWithoutStoppingRuntime));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var coordinator = new KimiWebWorkspaceCoordinator(supervisor, bridge);

        var id = (await coordinator.CreateAsync())!.Value;
        Assert.AreEqual(id, coordinator.OpenWorkspaceId);
        Assert.IsTrue(bridge.Events.Any(
            message => message.GetProperty("type").GetString() == "kimi_web_runtime_status"
                       && message.GetProperty("state").GetString() == "unavailable"));

        // Closing the tab only clears the descriptor; the supervisor is not
        // told to stop (background work survives).
        await coordinator.CloseAsync(id, WorkspaceCloseReason.User);
        Assert.IsNull(coordinator.OpenWorkspaceId);
        Assert.AreEqual(KimiWebRuntimeState.Unavailable, supervisor.State,
            "closing the tab must not stop the runtime");

        // Reopen yields a fresh id without touching the supervisor.
        var second = (await coordinator.CreateAsync())!.Value;
        Assert.AreNotEqual(id, second);
    }

    [TestMethod]
    public async Task HandleCommand_RetryThenStop_RoutesToSupervisor()
    {
        using var workspace = TestWorkspace.Create(nameof(HandleCommand_RetryThenStop_RoutesToSupervisor));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var coordinator = new KimiWebWorkspaceCoordinator(supervisor, bridge);

        await supervisor.StartAsync();
        Assert.AreEqual(KimiWebRuntimeState.Unavailable, supervisor.State);

        // retry re-validates only: the state stays unavailable with the same
        // reason and no process is started.
        await coordinator.HandleCommandAsync("retry");
        Assert.AreEqual(KimiWebRuntimeState.Unavailable, supervisor.State);

        await coordinator.HandleCommandAsync("stop");
        Assert.AreEqual(KimiWebRuntimeState.Stopped, supervisor.State);
    }

    [TestMethod]
    public async Task HandleExport_NotReady_PublishesNoticeWithoutAnyNetworkCall()
    {
        using var workspace = TestWorkspace.Create(nameof(HandleExport_NotReady_PublishesNoticeWithoutAnyNetworkCall));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var coordinator = new KimiWebWorkspaceCoordinator(supervisor, bridge);

        await coordinator.HandleExportAsync(
            "http://127.0.0.1:1234/api/v1/sessions/s1/export", "/api/v1/sessions/s1/export", "s1");

        var notice = bridge.Events.Single(message => message.GetProperty("type").GetString() == "workspace_notice");
        Assert.AreEqual(WorkspaceNoticeCode.KimiExportNotReady, notice.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task HandleExport_ReadyButInvalidOrServerRefused_PublishesNoticesNeverSilently()
    {
        using var workspace = TestWorkspace.Create(nameof(HandleExport_ReadyButInvalidOrServerRefused_PublishesNoticesNeverSilently));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(
            KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.Full);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var coordinator = new KimiWebWorkspaceCoordinator(supervisor, bridge);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Ready,
            TimeSpan.FromSeconds(20),
            "the fake kimi web server did not reach Ready");
        var origin = KimiWebRuntimeSupervisor.ToFrameOrigin(supervisor.ReadyUrl)!;

        // Wrong origin: refused before any network call.
        await coordinator.HandleExportAsync(
            "http://127.0.0.1:9/api/v1/sessions/s1/export", "/api/v1/sessions/s1/export", "s1");
        AssertNotice(bridge, WorkspaceNoticeCode.KimiExportInvalidRequest);

        // 3xx response: the redirect is refused.
        await coordinator.HandleExportAsync(
            origin + "/api/v1/sessions/redirect/export", "/api/v1/sessions/redirect/export", "redirect");
        AssertNotice(bridge, WorkspaceNoticeCode.KimiExportInvalidResponse);

        // Non-ZIP magic: refused.
        await coordinator.HandleExportAsync(
            origin + "/api/v1/sessions/nonzip/export", "/api/v1/sessions/nonzip/export", "nonzip");
        AssertNotice(bridge, WorkspaceNoticeCode.KimiExportInvalidData);

        await supervisor.StopAsync();
        Assert.IsNull(supervisor.ReadyUrl);
    }

    // ---- helpers -----------------------------------------------------------

    private static void AssertNotice(RecordingAgentBridgeService bridge, string fragment)
    {
        var notice = bridge.Events.Last(message => message.GetProperty("type").GetString() == "workspace_notice");
        StringAssert.Contains(notice.GetProperty("code").GetString(), fragment);
    }

    private static KimiWebRuntimeSupervisor CreateSupervisor(
        TestWorkspace workspace, RecordingAgentBridgeService bridge) =>
        new(
            new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs")),
            bridge,
            Path.Combine(workspace.Path, "supervisor"),
            Path.Combine(workspace.Path, "kimi-web-workspace"));

    private static (KimiCodeAcpRuntime Runtime, RuntimePaths Paths) CreateRuntimeWithPaths(TestWorkspace workspace)
    {
        var locator = new RuntimeLocator(workspace.Path);
        var runtime = new KimiCodeAcpRuntime(locator, Path.Combine(workspace.Path, "logs"));
        return (runtime, locator.Locate());
    }

    private static string[] LeftoverScratchFiles(string directory, string target) =>
        Directory.GetFiles(directory, $".{Path.GetFileName(target)}.*.dsh-part");
}
