using System.Net;
using System.Net.Http;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiWebRuntimeSupervisorTests
{
    // ---- launch spec resolution / wire-safe helpers -----------------------

    [TestMethod]
    public void TryCreateWebLaunchSpec_PortableNodeMissing_ReportsPortableNodeMissing()
    {
        using var workspace = TestWorkspace.Create(nameof(TryCreateWebLaunchSpec_PortableNodeMissing_ReportsPortableNodeMissing));
        KimiWebTestFixture.InstallBundle(workspace.Path);
        File.Delete(Path.Combine(workspace.Path, "tools", "node", "node.exe"));
        var runtime = CreateRuntime(workspace);

        var spec = runtime.TryCreateWebLaunchSpec(workspace.Path, out var reason);

        Assert.IsNull(spec);
        Assert.AreEqual(KimiWebLaunchUnavailableReason.PortableNodeMissing, reason);
    }

    [TestMethod]
    public void TryCreateWebLaunchSpec_RuntimeMissing_ReportsRuntimeMissing()
    {
        using var workspace = TestWorkspace.Create(nameof(TryCreateWebLaunchSpec_RuntimeMissing_ReportsRuntimeMissing));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "tools", "node"));
        File.WriteAllText(Path.Combine(workspace.Path, "tools", "node", "node.exe"), "fake node");
        var runtime = CreateRuntime(workspace);

        var spec = runtime.TryCreateWebLaunchSpec(workspace.Path, out var reason);

        Assert.IsNull(spec);
        Assert.AreEqual(KimiWebLaunchUnavailableReason.RuntimeMissing, reason);
    }

    [TestMethod]
    public void TryCreateWebLaunchSpec_RuntimeInvalid_ReportsRuntimeInvalid()
    {
        using var workspace = TestWorkspace.Create(nameof(TryCreateWebLaunchSpec_RuntimeInvalid_ReportsRuntimeInvalid));
        KimiWebTestFixture.InstallBundle(workspace.Path);
        File.Delete(Path.Combine(
            workspace.Path, "tools", "kimi", "node_modules", "@moonshot-ai", "kimi-code", "package.json"));
        var runtime = CreateRuntime(workspace);

        var spec = runtime.TryCreateWebLaunchSpec(workspace.Path, out var reason);

        Assert.IsNull(spec);
        Assert.AreEqual(KimiWebLaunchUnavailableReason.RuntimeInvalid, reason);
    }

    [TestMethod]
    public void TryCreateWebLaunchSpec_ValidBundle_ReturnsAlignedLaunchSpec()
    {
        using var workspace = TestWorkspace.Create(nameof(TryCreateWebLaunchSpec_ValidBundle_ReturnsAlignedLaunchSpec));
        KimiWebTestFixture.InstallBundle(workspace.Path);
        var runtime = CreateRuntime(workspace);
        var workingDirectory = Path.Combine(workspace.Path, "kimi-web-workspace");

        var spec = runtime.TryCreateWebLaunchSpec(workingDirectory, out var reason);

        Assert.IsNotNull(spec);
        Assert.AreEqual(Path.Combine(workspace.Path, "tools", "node", "node.exe"), spec.NodePath);
        Assert.AreEqual(KimiWebTestFixture.EntryPath(workspace.Path), spec.EntryPath);
        Assert.AreEqual(workingDirectory, spec.WorkingDirectory);
        Assert.IsTrue(spec.Environment.ContainsKey("PATH"), "the launch environment mirrors the ACP pass-through");
        Assert.IsFalse(spec.Environment.ContainsKey("KIMI_CODE_HOME")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KIMI_CODE_HOME")),
            "only variables actually set in the process are forwarded");
    }

    [TestMethod]
    public void TryAcceptReadyUrl_AcceptsLoopbackHttpAndPreservesFragment()
    {
        Assert.IsTrue(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            $"http://127.0.0.1:41234/#token={KimiWebTestFixture.TestToken}", out var url));
        Assert.AreEqual("http://127.0.0.1:41234/#token=" + KimiWebTestFixture.TestToken, url.ToString());

        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl("https://127.0.0.1:41234/", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl("http://localhost:41234/", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl("http://evil.example:41234/", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl("http://user:pass@127.0.0.1:41234/", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            $"http://127.0.0.1:41234/app/#token={KimiWebTestFixture.TestToken}", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            $"http://127.0.0.1:41234/?next=x#token={KimiWebTestFixture.TestToken}", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            $"http://127.0.0.1/#token={KimiWebTestFixture.TestToken}", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            "http://127.0.0.1:41234/#token=short", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            $"http://127.0.0.1:41234/#token={KimiWebTestFixture.TestToken}&extra=x", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            $"http://127.0.0.1:41234/#token={new string('a', 513)}", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(new string('x', 2049), out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl(
            "http://127.0.0.1:41234/", out _));
        Assert.IsFalse(KimiWebRuntimeSupervisor.TryAcceptReadyUrl("not-a-url", out _));
    }

    [TestMethod]
    public void ToFrameOrigin_StripsFragmentAndKeepsSchemeHostPort()
    {
        Assert.AreEqual(
            "http://127.0.0.1:41234",
            KimiWebRuntimeSupervisor.ToFrameOrigin(new Uri($"http://127.0.0.1:41234/#token={KimiWebTestFixture.TestToken}")));
        Assert.IsNull(KimiWebRuntimeSupervisor.ToFrameOrigin(new Uri("http://evil.example:41234/")));
    }

    [TestMethod]
    public void BuildKimiWebFrameScript_SelfGatesOnOriginAndNeverCarriesToken()
    {
        var script = WebViewHostPolicy.BuildKimiWebFrameScript("http://127.0.0.1:41234");
        StringAssert.Contains(script, "http://127.0.0.1:41234");
        StringAssert.Contains(script, "location.origin");
        StringAssert.Contains(script, "psx-kimi-web-export");
        StringAssert.Contains(script, "psx-kimi-web-focus");
        Assert.IsFalse(script.Contains("token", StringComparison.OrdinalIgnoreCase),
            "the injected frame script must never carry the token");
    }

    // ---- supervisor state machine (no real process) -----------------------

    [TestMethod]
    public void InitialState_IsStopped_AndPublishesNothing()
    {
        using var workspace = TestWorkspace.Create(nameof(InitialState_IsStopped_AndPublishesNothing));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        Assert.AreEqual(KimiWebRuntimeState.Stopped, supervisor.State);
        Assert.IsEmpty(bridge.Events);
    }

    [TestMethod]
    public async Task StopAsync_WhenIdle_PublishesStopped()
    {
        using var workspace = TestWorkspace.Create(nameof(StopAsync_WhenIdle_PublishesStopped));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StopAsync();

        Assert.AreEqual(KimiWebRuntimeState.Stopped, supervisor.State);
        var statuses = KimiWebStatuses(bridge);
        Assert.AreEqual("stopping", statuses[0].GetProperty("state").GetString());
        Assert.AreEqual("stopped", statuses[1].GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task StartAsync_BadExecutable_CommitsFailedWithLaunchFailedError()
    {
        using var workspace = TestWorkspace.Create(nameof(StartAsync_BadExecutable_CommitsFailedWithLaunchFailedError));
        KimiWebTestFixture.InstallBundle(workspace.Path);
        File.WriteAllText(Path.Combine(workspace.Path, "tools", "node", "node.exe"), "not an executable");
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();

        Assert.AreEqual(KimiWebRuntimeState.Failed, supervisor.State);
        Assert.IsNull(supervisor.ReadyUrl);
        var status = KimiWebStatuses(bridge).Last();
        Assert.AreEqual("failed", status.GetProperty("state").GetString());
        Assert.AreEqual("launch_failed", status.GetProperty("errorClass").GetString());
    }

    [TestMethod]
    public async Task StartAsync_UnavailablePortableNodeMissing_PublishesWireReasonWithoutProcess()
    {
        using var workspace = TestWorkspace.Create(nameof(StartAsync_UnavailablePortableNodeMissing_PublishesWireReasonWithoutProcess));
        KimiWebTestFixture.InstallBundle(workspace.Path);
        File.Delete(Path.Combine(workspace.Path, "tools", "node", "node.exe"));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();

        Assert.AreEqual(KimiWebRuntimeState.Unavailable, supervisor.State);
        var status = KimiWebStatuses(bridge).Single();
        Assert.AreEqual("unavailable", status.GetProperty("state").GetString());
        Assert.AreEqual("portable_node_missing", status.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task RetryAfterUnavailable_OnlyRevalidatesWithoutLaunching()
    {
        using var workspace = TestWorkspace.Create(nameof(RetryAfterUnavailable_OnlyRevalidatesWithoutLaunching));
        // A structurally invalid bundled install: retry must re-resolve the
        // spec and re-publish the reason, never start a process or download.
        KimiWebTestFixture.InstallBundle(workspace.Path);
        File.Delete(KimiWebTestFixture.EntryPath(workspace.Path));
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();
        await supervisor.RetryAsync();

        Assert.AreEqual(KimiWebRuntimeState.Unavailable, supervisor.State);
        Assert.IsNull(supervisor.ReadyUrl);
        var statuses = KimiWebStatuses(bridge);
        Assert.HasCount(2, statuses);
        foreach (var status in statuses)
        {
            Assert.AreEqual("unavailable", status.GetProperty("state").GetString());
            Assert.AreEqual("runtime_invalid", status.GetProperty("reason").GetString());
            Assert.AreEqual(JsonValueKind.Null, status.GetProperty("errorClass").ValueKind);
        }
    }

    // ---- supervisor with the real Node toolchain (fake kimi web server) ----

    [TestMethod]
    public async Task Supervisor_ReadyDualChannel_BroadcastsFragmentUrlAndStripsOrigin()
    {
        using var workspace = TestWorkspace.Create(nameof(Supervisor_ReadyDualChannel_BroadcastsFragmentUrlAndStripsOrigin));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.Full);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var origins = new List<string?>();
        supervisor.FrameOriginChanged += origin => origins.Add(origin);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State is KimiWebRuntimeState.Ready or KimiWebRuntimeState.Failed,
            TimeSpan.FromSeconds(20),
            "the fake kimi web server did not reach a terminal startup state");

        Assert.AreEqual(KimiWebRuntimeState.Ready, supervisor.State, bridge.Events.LastOrDefault().ToString());
        Assert.IsNotNull(supervisor.ReadyUrl);
        Assert.IsTrue(supervisor.ReadyUrl.ToString().Contains("#token=" + KimiWebTestFixture.TestToken, StringComparison.Ordinal));

        // Dual channel: the broadcast carries the fragment-bearing readyUrl,
        // the frame-origin callback carries only the fragment-free origin.
        var ready = KimiWebStatuses(bridge).Last(status => status.GetProperty("state").GetString() == "ready");
        var broadcastUrl = ready.GetProperty("readyUrl").GetString();
        Assert.AreEqual(supervisor.ReadyUrl.ToString(), broadcastUrl);
        StringAssert.Contains(broadcastUrl, "#token=" + KimiWebTestFixture.TestToken);

        var origin = KimiWebRuntimeSupervisor.ToFrameOrigin(supervisor.ReadyUrl);
        Assert.AreEqual(origin, origins.Last());
        Assert.IsNotNull(origin);
        Assert.DoesNotContain("#", origin, "the frame origin never carries the token fragment");
        Assert.DoesNotContain(KimiWebTestFixture.TestToken, string.Join(",", origins),
            "the token never reaches the frame whitelist");

        await supervisor.StopAsync();
    }

    [TestMethod]
    public async Task Supervisor_AwaitsFramePreparationBeforePublishingReady()
    {
        using var workspace = TestWorkspace.Create(nameof(Supervisor_AwaitsFramePreparationBeforePublishingReady));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(
            KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.Full);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.FrameOriginPreparingAsync = async origin =>
        {
            Assert.AreEqual("http", new Uri(origin).Scheme);
            preparationEntered.TrySetResult();
            await releasePreparation.Task.ConfigureAwait(false);
        };

        await supervisor.StartAsync();
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(KimiWebRuntimeState.Starting, supervisor.State);
        Assert.IsFalse(KimiWebStatuses(bridge).Any(
            status => status.GetProperty("state").GetString() == "ready"),
            "Ready must not cross the bridge before document-start registration completes");

        releasePreparation.TrySetResult();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Ready,
            TimeSpan.FromSeconds(20),
            "the supervisor did not publish Ready after frame preparation completed");

        await supervisor.StopAsync();
    }

    [TestMethod]
    public async Task StopDuringFramePreparation_PublishesOnlyStoppingThenStopped()
    {
        using var workspace = TestWorkspace.Create(nameof(StopDuringFramePreparation_PublishesOnlyStoppingThenStopped));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(
            KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.Full);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.FrameOriginPreparingAsync = async _ =>
        {
            preparationEntered.TrySetResult();
            await releasePreparation.Task.ConfigureAwait(false);
        };

        await supervisor.StartAsync();
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var stop = supervisor.StopAsync();
        releasePreparation.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(KimiWebRuntimeState.Stopped, supervisor.State);
        var states = KimiWebStatuses(bridge)
            .Select(status => status.GetProperty("state").GetString())
            .ToArray();
        CollectionAssert.DoesNotContain(states, "ready");
        CollectionAssert.DoesNotContain(states, "failed");
        CollectionAssert.AreEqual(new[] { "starting", "stopping", "stopped" }, states);
        Assert.IsTrue(supervisor.CleanupTask.IsCompleted,
            "Stop must not return while the canceled generation still owns process resources");
    }

    [TestMethod]
    public async Task Token_NeverAppearsOutsideTheReadyReadyUrl()
    {
        using var workspace = TestWorkspace.Create(nameof(Token_NeverAppearsOutsideTheReadyReadyUrl));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.Full);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Ready,
            TimeSpan.FromSeconds(20),
            "the fake kimi web server did not reach Ready");

        // Only the Ready status's readyUrl field may carry the token; every
        // other status field and every errorClass key stays token-free.
        foreach (var status in KimiWebStatuses(bridge))
        {
            var state = status.GetProperty("state").GetString();
            var text = status.ToString();
            if (state == "ready")
            {
                Assert.AreEqual(supervisor.ReadyUrl!.ToString(), status.GetProperty("readyUrl").GetString());
                Assert.IsTrue(text.Contains(KimiWebTestFixture.TestToken, StringComparison.Ordinal));
            }
            else
            {
                Assert.IsFalse(text.Contains(KimiWebTestFixture.TestToken, StringComparison.Ordinal),
                    $"token leaked into a non-ready status: {text}");
            }
        }

        // The supervisor log records origins only — never the token.
        await TestWorkspace.WaitUntilAsync(
            () => File.Exists(Path.Combine(workspace.Path, "supervisor", "kimi-web-supervisor.log"))
                  && File.ReadAllText(Path.Combine(workspace.Path, "supervisor", "kimi-web-supervisor.log"))
                      .Contains("Kimi web ready at", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            "the ready diagnostic line was not logged");
        var log = File.ReadAllText(Path.Combine(workspace.Path, "supervisor", "kimi-web-supervisor.log"));
        Assert.IsFalse(log.Contains(KimiWebTestFixture.TestToken, StringComparison.Ordinal),
            "the token must never be logged");

        await supervisor.StopAsync();
        Assert.IsNull(supervisor.ReadyUrl, "stop clears the token-bearing ready URL immediately");
    }

    [TestMethod]
    public async Task StopAfterReady_UsesGracefulShutdownWithBearerToken()
    {
        using var workspace = TestWorkspace.Create(nameof(StopAfterReady_UsesGracefulShutdownWithBearerToken));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.Full);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Ready,
            TimeSpan.FromSeconds(20),
            "the fake kimi web server did not reach Ready");

        await supervisor.StopAsync();

        Assert.AreEqual(KimiWebRuntimeState.Stopped, supervisor.State);
        Assert.IsNull(supervisor.ReadyUrl);
        var last = KimiWebStatuses(bridge).Last();
        Assert.AreEqual("stopped", last.GetProperty("state").GetString());
        Assert.AreEqual(JsonValueKind.Null, last.GetProperty("readyUrl").ValueKind);

        // The graceful shutdown request carried the in-memory token as a
        // Bearer header; the fake server recorded it beside its working dir.
        var headerLog = Path.Combine(workspace.Path, "kimi-web-workspace", "shutdown-headers.log");
        await TestWorkspace.WaitUntilAsync(
            () => File.Exists(headerLog),
            TimeSpan.FromSeconds(10),
            "the fake server did not record the shutdown request");
        Assert.AreEqual($"Bearer {KimiWebTestFixture.TestToken}", File.ReadAllText(headerLog).Trim());
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(workspace.Path, "supervisor", "kimi-web-supervisor.log")),
            "shut down gracefully");
    }

    [TestMethod]
    public async Task StopFallsBackToKillWhenGracefulShutdownIsUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(StopFallsBackToKillWhenGracefulShutdownIsUnavailable));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.NoShutdown);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Ready,
            TimeSpan.FromSeconds(20),
            "the fake kimi web server did not reach Ready");

        await supervisor.StopAsync();

        Assert.AreEqual(KimiWebRuntimeState.Stopped, supervisor.State);
        Assert.IsTrue(supervisor.CleanupTask.IsCompleted,
            "Stop must finish lease/process/pipe cleanup before publishing Stopped");
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(workspace.Path, "supervisor", "kimi-web-supervisor.log")),
            "killing the process tree");
    }

    [TestMethod]
    public async Task ReadyThenUnexpectedExit_CommitsExited()
    {
        using var workspace = TestWorkspace.Create(nameof(ReadyThenUnexpectedExit_CommitsExited));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.ExitAfterReady);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);
        var clearedOrigins = new List<string?>();
        supervisor.FrameOriginChanged += origin => clearedOrigins.Add(origin);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Ready,
            TimeSpan.FromSeconds(20),
            "the fake kimi web server did not reach Ready");

        // The server exits on its own after the ready window: the supervisor
        // must commit the terminal Exited state, not Failed.
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State == KimiWebRuntimeState.Exited
                  && KimiWebStatuses(bridge).Any(status => status.GetProperty("state").GetString() == "exited"),
            TimeSpan.FromSeconds(20),
            "an unexpected process exit must commit Exited");
        Assert.IsNull(supervisor.ReadyUrl);
        Assert.IsNull(clearedOrigins.Last(), "the frame origin is revoked on exit");
        Assert.AreEqual("exited", KimiWebStatuses(bridge).Last().GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task HealthCheckFailure_CommitsFailedWithHealthCheckFailedError()
    {
        using var workspace = TestWorkspace.Create(nameof(HealthCheckFailure_CommitsFailedWithHealthCheckFailedError));
        var (runtime, paths) = CreateRuntimeWithPaths(workspace);
        KimiWebTestFixture.InstallBundle(workspace.Path);
        KimiWebTestFixture.SeedRealNode(paths);
        KimiWebTestFixture.WriteFakeKimiWebEntry(KimiWebTestFixture.EntryPath(workspace.Path), KimiWebTestFixture.FakeServerMode.MetaRejects);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = CreateSupervisor(workspace, bridge);

        await supervisor.StartAsync();
        await TestWorkspace.WaitUntilAsync(
            () => supervisor.State is KimiWebRuntimeState.Ready or KimiWebRuntimeState.Failed
                  && KimiWebStatuses(bridge).Any(status => status.GetProperty("state").GetString() == "failed"),
            TimeSpan.FromSeconds(20),
            "the failing server did not reach a terminal startup state");

        Assert.AreEqual(KimiWebRuntimeState.Failed, supervisor.State);
        Assert.IsNull(supervisor.ReadyUrl);
        Assert.AreEqual("health_check_failed",
            KimiWebStatuses(bridge).First(status => status.GetProperty("state").GetString() == "failed")
                .GetProperty("errorClass").GetString());
    }

    // ---- launcher environment overrides ------------------------------------

    [TestMethod]
    public async Task TryStartInJob_EnvironmentOverridesReachChildProcess()
    {
        using var workspace = TestWorkspace.Create(nameof(TryStartInJob_EnvironmentOverridesReachChildProcess));
        var diagnostics = new List<string>();
        var overrides = new Dictionary<string, string>
        {
            ["PSX_KIMI_WEB_TEST_ENV"] = "override-value-42",
            ["TEMP"] = "KIMI_WEB_TEST_TEMP_OVERRIDE"
        };
        var launch = SuspendedJobProcessLauncher.TryStartInJob(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            new[] { "/d", "/c", "set" },
            workspace.Path,
            diagnostics.Add,
            overrides);

        Assert.IsTrue(launch.Succeeded,
            launch.FailureDetail ?? string.Join(Environment.NewLine, diagnostics));
        try
        {
            var outputTask = launch.Output!.ReadToEndAsync();
            var errorTask = launch.Error!.ReadToEndAsync();
            await launch.Process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output = await outputTask.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await errorTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(0, launch.Process.ExitCode, error);
            StringAssert.Contains(output, "PSX_KIMI_WEB_TEST_ENV=override-value-42");
            StringAssert.Contains(output, "TEMP=KIMI_WEB_TEST_TEMP_OVERRIDE");
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            Assert.IsNotNull(systemRoot);
            StringAssert.Contains(output, "SystemRoot=" + systemRoot, StringComparison.OrdinalIgnoreCase,
                "the merged block inherits the parent environment");
        }
        finally
        {
            launch.Output?.Dispose();
            launch.Error?.Dispose();
            if (launch.Process != null)
            {
                try
                {
                    if (!launch.Process.HasExited)
                        launch.Process.Kill(entireProcessTree: true);
                }
                catch { }
                launch.Process.Dispose();
            }
            launch.Job?.Dispose();
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static KimiWebRuntimeSupervisor CreateSupervisor(
        TestWorkspace workspace, RecordingAgentBridgeService bridge)
    {
        var runtime = CreateRuntime(workspace);
        return new KimiWebRuntimeSupervisor(
            runtime,
            bridge,
            Path.Combine(workspace.Path, "supervisor"),
            Path.Combine(workspace.Path, "kimi-web-workspace"));
    }

    private static KimiCodeAcpRuntime CreateRuntime(TestWorkspace workspace) =>
        new(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));

    private static (KimiCodeAcpRuntime Runtime, RuntimePaths Paths) CreateRuntimeWithPaths(TestWorkspace workspace)
    {
        var locator = new RuntimeLocator(workspace.Path);
        var runtime = new KimiCodeAcpRuntime(locator, Path.Combine(workspace.Path, "logs"));
        return (runtime, locator.Locate());
    }

    private static System.Text.Json.JsonElement[] KimiWebStatuses(RecordingAgentBridgeService bridge) =>
        bridge.Events
            .Where(message => message.GetProperty("type").GetString() == "kimi_web_runtime_status")
            .ToArray();
}
