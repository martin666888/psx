using System.Net;
using System.Net.Http;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DshRuntimeLifecycleTests
{
    [TestMethod]
    public void TrySwapToCurrent_MovesScratchAndWritesPointer()
    {
        using var workspace = TestWorkspace.Create(nameof(TrySwapToCurrent_MovesScratchAndWritesPointer));
        var (runtime, paths) = CreateRuntime(workspace);
        Directory.CreateDirectory(paths.DshInstallingDirectory);
        File.WriteAllText(Path.Combine(paths.DshInstallingDirectory, "ready"), "new");
        Directory.CreateDirectory(paths.DshCurrentDirectory);
        File.WriteAllText(Path.Combine(paths.DshCurrentDirectory, "ready"), "old");

        Assert.IsTrue(runtime.TrySwapToCurrent(paths, paths.DshInstallingDirectory));

        Assert.AreEqual("new", File.ReadAllText(Path.Combine(paths.DshCurrentDirectory, "ready")));
        Assert.IsFalse(Directory.Exists(paths.DshInstallingDirectory));
        Assert.AreEqual("current", File.ReadAllText(paths.DshActivePointerFile));
    }

    [TestMethod]
    public void TrySwapToCurrent_PointerWriteFailure_StillCommitsNewCurrent()
    {
        using var workspace = TestWorkspace.Create(nameof(TrySwapToCurrent_PointerWriteFailure_StillCommitsNewCurrent));
        var (runtime, paths) = CreateRuntime(workspace);
        Directory.CreateDirectory(paths.DshInstallingDirectory);
        File.WriteAllText(Path.Combine(paths.DshInstallingDirectory, "ready"), "new");
        Directory.CreateDirectory(paths.DshCurrentDirectory);
        File.WriteAllText(Path.Combine(paths.DshCurrentDirectory, "ready"), "old");
        Directory.CreateDirectory(paths.DshActivePointerFile);

        Assert.IsTrue(runtime.TrySwapToCurrent(paths, paths.DshInstallingDirectory));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(paths.DshCurrentDirectory, "ready")));
        Assert.IsTrue(Directory.Exists(paths.DshActivePointerFile));
        Assert.IsFalse(File.Exists(paths.DshActivePointerFile));
    }

    [TestMethod]
    public void TrySwapToCurrent_FailedMove_DoesNotWritePointerOrReportSuccess()
    {
        using var workspace = TestWorkspace.Create(nameof(TrySwapToCurrent_FailedMove_DoesNotWritePointerOrReportSuccess));
        var (runtime, paths) = CreateRuntime(workspace);
        Directory.CreateDirectory(paths.DshInstallingDirectory);
        File.WriteAllText(Path.Combine(paths.DshInstallingDirectory, "ready"), "new");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DshCurrentDirectory)!);
        File.WriteAllText(paths.DshCurrentDirectory, "blocked");

        Assert.IsFalse(runtime.TrySwapToCurrent(paths, paths.DshInstallingDirectory));

        Assert.IsFalse(File.Exists(paths.DshActivePointerFile));
        Assert.IsTrue(File.Exists(paths.DshCurrentDirectory));
        Assert.AreEqual("blocked", File.ReadAllText(paths.DshCurrentDirectory));
        Assert.IsTrue(Directory.Exists(paths.DshInstallingDirectory));
    }

    [TestMethod]
    public void PrepareForStartup_PointerNext_PromotesToCurrentWithoutNetwork()
    {
        using var workspace = TestWorkspace.Create(nameof(PrepareForStartup_PointerNext_PromotesToCurrentWithoutNetwork));
        var (runtime, paths) = CreateRuntime(workspace);
        SeedFakeDshTree(paths.DshNextDirectory, DshWebRuntime.SeededPackageVersion);
        File.WriteAllText(Path.Combine(paths.DshNextDirectory, "ready"), "staged");
        Directory.CreateDirectory(paths.RuntimeRoot);
        File.WriteAllText(paths.DshActivePointerFile, "next");

        runtime.PrepareForStartup();

        Assert.AreEqual("staged", File.ReadAllText(Path.Combine(paths.DshCurrentDirectory, "ready")));
        Assert.IsFalse(Directory.Exists(paths.DshNextDirectory));
        Assert.AreEqual("current", File.ReadAllText(paths.DshActivePointerFile));
        Assert.IsTrue(runtime.IsInstalled(), "the promoted tree carries the seeded version");
    }

    [TestMethod]
    public void PrepareForStartup_NextVersionMismatch_DiscardsCandidateAndKeepsCurrent()
    {
        using var workspace = TestWorkspace.Create(nameof(PrepareForStartup_NextVersionMismatch_DiscardsCandidateAndKeepsCurrent));
        var (runtime, paths) = CreateRuntime(workspace);
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        File.WriteAllText(Path.Combine(paths.DshCurrentDirectory, "ready"), "old");
        SeedFakeDshTree(paths.DshNextDirectory, "9.9.9-fake");
        File.WriteAllText(Path.Combine(paths.DshNextDirectory, "ready"), "staged");
        Directory.CreateDirectory(paths.RuntimeRoot);
        File.WriteAllText(paths.DshActivePointerFile, "next");

        runtime.PrepareForStartup();

        Assert.AreEqual("old", File.ReadAllText(Path.Combine(paths.DshCurrentDirectory, "ready")),
            "a mismatched staged candidate must not be promoted");
        Assert.IsTrue(runtime.IsInstalled());
        Assert.IsFalse(Directory.Exists(paths.DshNextDirectory), "the mismatched candidate is discarded");
        Assert.AreEqual("current", File.ReadAllText(paths.DshActivePointerFile),
            "the pointer is reset so startup does not retry the bad candidate forever");
    }

    [TestMethod]
    public void CreateLaunchSpec_WrongVersionCurrent_IsRefusedUntilReinstalled()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateLaunchSpec_WrongVersionCurrent_IsRefusedUntilReinstalled));
        var (runtime, paths) = CreateRuntime(workspace);
        SeedFakeNodeToolchain(paths);
        SeedFakeDshTree(paths.DshCurrentDirectory, "9.9.9-fake");

        Assert.IsFalse(runtime.IsInstalled());
        Assert.IsNull(runtime.CreateLaunchSpec(), "a stale or hand-replaced dsh-current must not start");

        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        Assert.IsTrue(runtime.IsInstalled());
        Assert.IsNotNull(runtime.CreateLaunchSpec());
    }

    [TestMethod]
    public void GenerationGate_InvalidateDropsPriorStartToken()
    {
        var gate = new DshRuntimeGenerationGate();
        var first = gate.Begin();
        Assert.IsTrue(gate.IsCurrent(first));

        gate.Invalidate();
        Assert.IsFalse(gate.IsCurrent(first));

        var second = gate.Begin();
        Assert.IsTrue(gate.IsCurrent(second));
        Assert.IsFalse(gate.IsCurrent(first));
    }

    [TestMethod]
    public async Task TryCommitProcessState_StaleGeneration_DoesNotPublishReady()
    {
        using var workspace = TestWorkspace.Create(nameof(TryCommitProcessState_StaleGeneration_DoesNotPublishReady));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        Uri? origin = new Uri("http://127.0.0.1:1/");
        supervisor.ReadyUrlChanged = url => origin = url;
        var ready = new Uri("http://127.0.0.1:4321/");

        Assert.IsFalse(supervisor.TryCommitProcessState(1, DshRuntimeState.Ready, ready));
        Assert.AreEqual(DshRuntimeState.NotInstalled, supervisor.State);
        Assert.IsNull(supervisor.ReadyUrl);
        Assert.AreEqual(new Uri("http://127.0.0.1:1/"), origin);

        await supervisor.StopAsync();
        Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
        Assert.IsNull(origin);
        Assert.IsFalse(supervisor.TryCommitProcessState(0, DshRuntimeState.Ready, ready));
        Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
        Assert.IsNull(supervisor.ReadyUrl);
    }

    [TestMethod]
    public async Task HandleCommand_InstallFailure_KeepsFailedAndIsNotOverwrittenByEnsureRunning()
    {
        using var workspace = TestWorkspace.Create(nameof(HandleCommand_InstallFailure_KeepsFailedAndIsNotOverwrittenByEnsureRunning));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        var coordinator = new DshWebWorkspaceCoordinator(supervisor, bridge);

        await coordinator.HandleCommandAsync("install");

        Assert.AreEqual(DshRuntimeState.Failed, supervisor.State);
        var status = LastRuntimeStatus(bridge);
        Assert.AreEqual("failed", status.GetProperty("state").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(status.GetProperty("errorClass").GetString()));

        await supervisor.EnsureRunningAsync();
        Assert.AreEqual(DshRuntimeState.Failed, supervisor.State);
        Assert.AreEqual("failed", LastRuntimeStatus(bridge).GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task HandleCommand_ConcurrentInstallAndStop_EndsInFailedOrExited()
    {
        using var workspace = TestWorkspace.Create(nameof(HandleCommand_ConcurrentInstallAndStop_EndsInFailedOrExited));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        var coordinator = new DshWebWorkspaceCoordinator(supervisor, bridge);

        var install = coordinator.HandleCommandAsync("install");
        var stop = supervisor.StopAsync();
        await Task.WhenAll(install, stop);

        Assert.IsTrue(
            supervisor.State is DshRuntimeState.Failed or DshRuntimeState.Exited,
            $"expected failed or exited, got {supervisor.State}");
        var status = LastRuntimeStatus(bridge).GetProperty("state").GetString();
        Assert.IsTrue(status is "failed" or "exited", $"last wire state was {status}");
        Assert.AreNotEqual("not_installed", status);
        Assert.AreNotEqual("ready", status);
    }

    [TestMethod]
    public async Task HandleCommand_SerializedDuplicateInstall_StaysFailed()
    {
        using var workspace = TestWorkspace.Create(nameof(HandleCommand_SerializedDuplicateInstall_StaysFailed));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        var coordinator = new DshWebWorkspaceCoordinator(supervisor, bridge);

        await Task.WhenAll(
            coordinator.HandleCommandAsync("install"),
            coordinator.HandleCommandAsync("install"),
            coordinator.HandleCommandAsync("retry"));

        Assert.AreEqual(DshRuntimeState.Failed, supervisor.State);
        Assert.AreEqual("failed", LastRuntimeStatus(bridge).GetProperty("state").GetString());
    }

    [TestMethod]
    public void TryCommitProcessState_StopWaitsForInFlightReadyPublish()
    {
        using var workspace = TestWorkspace.Create(nameof(TryCommitProcessState_StopWaitsForInFlightReadyPublish));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        supervisor.ReadyUrlChanged = url =>
        {
            if (url == null)
                return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                Assert.Fail("Ready publish was not released.");
        };
        var ready = new Uri("http://127.0.0.1:4321/");

        var commit = Task.Run(() => supervisor.TryCommitProcessState(0, DshRuntimeState.Ready, ready));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "Ready publish did not start");
            var stop = Task.Run(() => supervisor.StopAsync());
            Assert.IsFalse(stop.Wait(TimeSpan.FromMilliseconds(250)), "Stop published before the in-flight Ready callback finished");
            release.Set();
            Assert.IsTrue(commit.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(commit.Result);
            Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
            Assert.IsNull(supervisor.ReadyUrl);
        }
        finally
        {
            release.Set();
        }
    }

    [TestMethod]
    public async Task TryReadExactAsync_FillsPartialReadsAndRejectsShortStreams()
    {
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        var prefix = new byte[4];
        await using (var source = new OneByteAtATimeStream(zip))
        {
            Assert.IsTrue(await DshWebWorkspaceCoordinator.TryReadExactAsync(source, prefix));
            CollectionAssert.AreEqual(zip, prefix);
        }

        prefix = new byte[4];
        await using (var source = new OneByteAtATimeStream(new byte[] { 0x50, 0x4B }))
            Assert.IsFalse(await DshWebWorkspaceCoordinator.TryReadExactAsync(source, prefix));
    }

    [TestMethod]
    public void IsAllowedExportUri_AcceptsCurrentOriginExportPathOnly()
    {
        var ready = new Uri("http://127.0.0.1:4321/");
        Assert.IsTrue(DshWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:4321/api/session.export?sessionId=s1"), ready));
        Assert.IsFalse(DshWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:9/api/session.export"), ready));
        Assert.IsFalse(DshWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("http://127.0.0.1:4321/api/other"), ready));
        Assert.IsFalse(DshWebWorkspaceCoordinator.IsAllowedExportUri(
            new Uri("https://127.0.0.1:4321/api/session.export"), ready));
    }

    [TestMethod]
    public void ShouldRejectExportResponse_RejectsRedirectAndCrossOriginFinalUri()
    {
        var ready = new Uri("http://127.0.0.1:4321/");
        using var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get, "http://127.0.0.1:4321/api/session.export"),
            Headers = { Location = new Uri("http://evil.example/steal") }
        };
        Assert.IsTrue(DshWebWorkspaceCoordinator.ShouldRejectExportResponse(redirect, ready));

        using var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get, "http://127.0.0.1:4321/api/session.export")
        };
        Assert.IsFalse(DshWebWorkspaceCoordinator.ShouldRejectExportResponse(ok, ready));

        using var hopped = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get, "http://127.0.0.1:9/api/session.export")
        };
        Assert.IsTrue(DshWebWorkspaceCoordinator.ShouldRejectExportResponse(hopped, ready));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:4321/", "http://127.0.0.1:4321", true)]
    [DataRow("http://127.0.0.1:4321/session", "http://127.0.0.1:4321", true)]
    [DataRow("http://127.0.0.1:9/", "http://127.0.0.1:4321", false)]
    [DataRow("https://127.0.0.1:4321/", "http://127.0.0.1:4321", false)]
    [DataRow("about:blank", "http://127.0.0.1:4321", false)]
    [DataRow("", "http://127.0.0.1:4321", false)]
    [DataRow("http://127.0.0.1:4321/", "", false)]
    [DataRow(null, "http://127.0.0.1:4321", false)]
    public void IsAllowedDshFrameSource_MatchesCurrentOriginOnly(string? source, string origin, bool expected)
    {
        Assert.AreEqual(expected, WebViewHostPolicy.IsAllowedDshFrameSource(source, origin));
    }

    [TestMethod]
    public void BuildDshFrameScript_SelfGatesOnSerializedOrigin()
    {
        var script = WebViewHostPolicy.BuildDshFrameScript("http://127.0.0.1:4321");
        StringAssert.Contains(script, "http://127.0.0.1:4321");
        StringAssert.Contains(script, "location.origin");
        StringAssert.Contains(script, "psx-dsh-export");
    }

    [TestMethod]
    public async Task InstallAndStartAsync_ConcurrentCalls_JoinOneRun()
    {
        using var workspace = TestWorkspace.Create(nameof(InstallAndStartAsync_ConcurrentCalls_JoinOneRun));
        var (runtime, paths) = CreateRuntime(workspace);
        SeedRealProcessToolchain(paths);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));

        // Single-flight: while one install is in flight, concurrent requests
        // join it instead of queueing repeated npm ci executions. The fake
        // toolchain launches a real (short-lived) process so the install is
        // genuinely asynchronous and the join window is deterministic.
        var first = supervisor.InstallAndStartAsync();
        var second = supervisor.InstallAndStartAsync();
        var retry = supervisor.RetryAsync();

        Assert.AreSame(first, second);
        Assert.AreSame(first, retry);
        await Task.WhenAll(first, second, retry);
        Assert.AreEqual(DshRuntimeState.Failed, supervisor.State);

        // After the run completes a fresh request starts a new run.
        var next = supervisor.InstallAndStartAsync();
        Assert.AreNotSame(first, next);
        await next;
    }

    [TestMethod]
    public async Task StopAsync_CancelsNothingWhenIdle_AndPublishesExited()
    {
        using var workspace = TestWorkspace.Create(nameof(StopAsync_CancelsNothingWhenIdle_AndPublishesExited));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));

        await supervisor.StopAsync();

        Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
        Assert.AreEqual("exited", LastRuntimeStatus(bridge).GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task SaveExportAtomicallyAsync_ReplacesExistingTargetOnlyAfterFullWrite()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveExportAtomicallyAsync_ReplacesExistingTargetOnlyAfterFullWrite));
        var target = Path.Combine(workspace.Path, "session.zip");
        File.WriteAllText(target, "previous export");
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        var prefix = payload[..4];

        await DshWebWorkspaceCoordinator.SaveExportAtomicallyAsync(
            new MemoryStream(payload[4..]), prefix, target, DshWebWorkspaceCoordinator.MaximumExportBytes);

        CollectionAssert.AreEqual(payload, File.ReadAllBytes(target));
        Assert.IsEmpty(LeftoverScratchFiles(workspace.Path, target));
    }

    [TestMethod]
    public async Task SaveExportAtomicallyAsync_MidWriteFailure_KeepsExistingTarget()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveExportAtomicallyAsync_MidWriteFailure_KeepsExistingTarget));
        var target = Path.Combine(workspace.Path, "session.zip");
        File.WriteAllText(target, "previous export");

        await Assert.ThrowsAsync<IOException>(() =>
            DshWebWorkspaceCoordinator.SaveExportAtomicallyAsync(
                new ThrowingStream(new byte[] { 1, 2, 3, 4 }), new byte[] { 0x50, 0x4B, 0x03, 0x04 },
                target, DshWebWorkspaceCoordinator.MaximumExportBytes));

        Assert.AreEqual("previous export", File.ReadAllText(target));
        Assert.IsEmpty(LeftoverScratchFiles(workspace.Path, target));
    }

    [TestMethod]
    public async Task SaveExportAtomicallyAsync_OverSizeLimit_ThrowsAndKeepsExistingTarget()
    {
        using var workspace = TestWorkspace.Create(nameof(SaveExportAtomicallyAsync_OverSizeLimit_ThrowsAndKeepsExistingTarget));
        var target = Path.Combine(workspace.Path, "session.zip");
        File.WriteAllText(target, "previous export");
        var source = new MemoryStream(new byte[4096]);

        await Assert.ThrowsAsync<DshWebWorkspaceCoordinator.DshExportTooLargeException>(() =>
            DshWebWorkspaceCoordinator.SaveExportAtomicallyAsync(
                source, new byte[] { 0x50, 0x4B, 0x03, 0x04 }, target, maximumBytes: 1024));

        Assert.AreEqual("previous export", File.ReadAllText(target));
        Assert.IsEmpty(LeftoverScratchFiles(workspace.Path, target));
    }

    [TestMethod]
    [DataRow("node.exe", new[] { "bin.js", "web" }, "node.exe bin.js web")]
    [DataRow(@"C:\bin dir\node.exe", new[] { @"d:\path with space\bin.js" }, @"""C:\bin dir\node.exe"" ""d:\path with space\bin.js""")]
    [DataRow("node.exe", new[] { "say \"hi\"" }, @"node.exe ""say \""hi\""""")]
    [DataRow("node.exe", new[] { @"trail dir\" }, @"node.exe ""trail dir\\""")]
    public void BuildCommandLine_QuotesWindowsArguments(string fileName, string[] arguments, string expected)
    {
        Assert.AreEqual(expected, SuspendedJobProcessLauncher.BuildCommandLine(fileName, arguments));
    }

    [TestMethod]
    public void ValidateInstalledTree_VersionMismatchWithSeed_IsRejectedBeforeSwap()
    {
        using var workspace = TestWorkspace.Create(nameof(ValidateInstalledTree_VersionMismatchWithSeed_IsRejectedBeforeSwap));
        var (runtime, paths) = CreateRuntime(workspace);
        var scratch = Path.Combine(workspace.Path, "scratch");
        SeedFakeDshTree(scratch, version: "9.9.9-fake");

        var result = runtime.ValidateInstalledTree(paths, scratch);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "\u9501\u5b9a\u7248\u672c");
        Assert.IsFalse(Directory.Exists(paths.DshCurrentDirectory),
            "a mismatched install must not swap onto dsh-current");
    }

    [TestMethod]
    public void ValidateInstalledTree_SeededVersion_SwapsOntoCurrent()
    {
        using var workspace = TestWorkspace.Create(nameof(ValidateInstalledTree_SeededVersion_SwapsOntoCurrent));
        var (runtime, paths) = CreateRuntime(workspace);
        var scratch = Path.Combine(workspace.Path, "scratch");
        SeedFakeDshTree(scratch, version: DshWebRuntime.SeededPackageVersion);
        Directory.CreateDirectory(paths.RuntimeRoot);

        var result = runtime.ValidateInstalledTree(paths, scratch);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsTrue(runtime.IsInstalled());
    }

    [TestMethod]
    public async Task InstallAsync_MissingSeedFile_FailsWithoutRunningNpm()
    {
        using var workspace = TestWorkspace.Create(nameof(InstallAsync_MissingSeedFile_FailsWithoutRunningNpm));
        var (runtime, paths) = CreateRuntime(workspace);
        SeedFakeNodeToolchain(paths);
        Directory.CreateDirectory(paths.DshSeedDirectory);
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package-lock.json"), "{}");
        // .npmrc intentionally absent.

        var result = await runtime.InstallAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "\u79cd\u5b50");
    }

    [TestMethod]
    public async Task RetryBurstThenStop_RunsOneInstallAndCancelsItPromptly()
    {
        using var workspace = TestWorkspace.Create(nameof(RetryBurstThenStop_RunsOneInstallAndCancelsItPromptly));
        var (runtime, paths) = CreateRuntime(workspace);
        var runsPath = Path.Combine(workspace.Path, "npm-runs.txt");
        SeedCountingSlowNpm(paths, runsPath, delayMs: 60_000);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        try
        {
            // Reviewer repro: retry #1 installs; retry #2 arrives while #1 holds
            // the lock; Stop cancels only what exists at that moment. With the
            // shared operation slot both retries are ONE run, so Stop cancels it
            // and never waits out a second uncancellable npm ci.
            var first = supervisor.RetryAsync();
            await TestWorkspace.WaitUntilAsync(
                () => File.Exists(runsPath) && File.ReadAllLines(runsPath).Length >= 1,
                TimeSpan.FromSeconds(15),
                "the fake npm never started");
            var second = supervisor.RetryAsync();
            Assert.AreSame(first, second, "retry joins the in-flight operation slot");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stop = supervisor.StopAsync();
            await Task.WhenAll(first, second, stop);
            watch.Stop();

            Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
            Assert.HasCount(1, File.ReadAllLines(runsPath),
                "exactly one npm ci may run for the whole retry burst");
            Assert.IsLessThan(30_000L, watch.ElapsedMilliseconds,
                $"Stop must cancel the install promptly, waited {watch.ElapsedMilliseconds}ms");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSX_DSH_TEST_RUNS", null);
        }
    }

    [TestMethod]
    public async Task StopDuringStopWindow_RejectsNewCommandsAndRecoversAfterwards()
    {
        using var workspace = TestWorkspace.Create(nameof(StopDuringStopWindow_RejectsNewCommandsAndRecoversAfterwards));
        var (runtime, paths) = CreateRuntime(workspace);
        var runsPath = Path.Combine(workspace.Path, "npm-runs.txt");
        SeedCountingSlowNpm(paths, runsPath, delayMs: 60_000);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        try
        {
            var installing = supervisor.RetryAsync();
            await TestWorkspace.WaitUntilAsync(
                () => File.Exists(runsPath) && File.ReadAllLines(runsPath).Length >= 1,
                TimeSpan.FromSeconds(15),
                "the fake npm never started");

            var stop = supervisor.StopAsync();
            // A retry racing the stop window is dropped instead of queueing.
            var raced = supervisor.RetryAsync();
            await Task.WhenAll(installing, stop, raced);
            Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
            Assert.HasCount(1, File.ReadAllLines(runsPath));

            // After the stop completes the window closes: a new command may
            // start a fresh run (observed via the run counter), and stopping
            // it stays prompt.
            var recovered = supervisor.RetryAsync();
            await TestWorkspace.WaitUntilAsync(
                () => File.Exists(runsPath) && File.ReadAllLines(runsPath).Length >= 2,
                TimeSpan.FromSeconds(15),
                "the post-stop retry did not start a new operation");
            Assert.HasCount(2, File.ReadAllLines(runsPath));
            var recoveryStop = supervisor.StopAsync();
            var recoveryWatch = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAll(recovered, recoveryStop);
            recoveryWatch.Stop();
            Assert.AreEqual(DshRuntimeState.Exited, supervisor.State);
            Assert.IsLessThan(30_000L, recoveryWatch.ElapsedMilliseconds,
                $"the recovered stop also cancelled promptly, waited {recoveryWatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSX_DSH_TEST_RUNS", null);
        }
    }

    [TestMethod]
    public void LeaseTearDown_NeverWaitsOnTasksContainingItsCaller()
    {
        var lease = new DshRuntimeLease(1);
        // Simulates MonitorAsync/WatchExitAsync being tracked while their own
        // commit chain runs TearDown: a pending task in the set used to cost a
        // fixed 5s self-wait under the publish lock.
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.TrackTask(pending.Task);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            lease.TearDown();
            watch.Stop();
            Assert.IsLessThan(2000L, watch.ElapsedMilliseconds,
                $"TearDown blocked {watch.ElapsedMilliseconds}ms on its own tracked tasks");
        }
        finally
        {
            pending.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CoordinatorBeginShutdown_RefusesFurtherCommandsAndExports()
    {
        using var workspace = TestWorkspace.Create(nameof(CoordinatorBeginShutdown_RefusesFurtherCommandsAndExports));
        var (runtime, _) = CreateRuntime(workspace);
        var bridge = new RecordingAgentBridgeService();
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"), Path.Combine(workspace.Path, "dsh-workspace"));
        var coordinator = new DshWebWorkspaceCoordinator(supervisor, bridge);

        coordinator.BeginShutdown();
        await coordinator.HandleCommandAsync("install");
        await coordinator.HandleCommandAsync("retry");
        await coordinator.HandleCommandAsync("stop");
        await coordinator.HandleExportAsync("http://127.0.0.1:4321/api/session.export", "s.zip");

        Assert.AreEqual(DshRuntimeState.NotInstalled, supervisor.State,
            "commands after BeginShutdown must not start anything");
        Assert.IsEmpty(bridge.Events.Where(message =>
            message.GetProperty("type").GetString() == "dsh_runtime_status"
            && message.GetProperty("state").GetString() == "installing"));
    }

    [TestMethod]
    public void RotatingDiagnosticLog_TruncatesSingleEntriesAndRotatesBeforeTheCap()
    {
        using var workspace = TestWorkspace.Create(nameof(RotatingDiagnosticLog_TruncatesSingleEntriesAndRotatesBeforeTheCap));
        var logPath = Path.Combine(workspace.Path, "bounded.log");

        // One oversized message is truncated instead of blowing past the cap.
        RotatingDiagnosticLog.AppendLine(logPath, new string('x', 2_000_000));
        Assert.IsLessThan(1_048_576L, new FileInfo(logPath).Length, "a single entry must be truncated");
        StringAssert.Contains(File.ReadAllText(logPath), "[truncated]");

        // Repeated appends rotate (restart) the file as soon as the next
        // entry would exceed the cap, keeping it a true hard bound. The
        // chunks stay below the single-entry truncation size.
        var previous = new FileInfo(logPath).Length;
        var rotated = false;
        for (var attempt = 0; attempt < 400 && !rotated; attempt++)
        {
            RotatingDiagnosticLog.AppendLine(logPath, new string('y', 4 * 1024));
            var current = new FileInfo(logPath).Length;
            rotated = current < previous;
            previous = current;
        }
        Assert.IsTrue(rotated, "appending past the cap must rotate the file");
        StringAssert.Contains(File.ReadAllText(logPath), "yyyy");
        Assert.IsLessThan(1_048_576L, new FileInfo(logPath).Length);
    }

    private static string[] LeftoverScratchFiles(string directory, string target) =>
        Directory.GetFiles(directory, $".{Path.GetFileName(target)}.*.dsh-part");

    private static void SeedFakeNodeToolchain(RuntimePaths paths)
    {
        // Fake portable node/npm so InstallAsync gets past the toolchain gate.
        var nodeDirectory = Path.Combine(paths.InstallDirectory, "tools", "node");
        Directory.CreateDirectory(Path.Combine(nodeDirectory, "node_modules", "npm", "bin"));
        File.WriteAllText(Path.Combine(nodeDirectory, "node.exe"), "fake");
        File.WriteAllText(Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js"), "fake");
    }

    private static void SeedRealProcessToolchain(RuntimePaths paths)
    {
        // The real Node 22 from TestResults (same toolchain the frontend
        // gates resolve) keeps the install genuinely asynchronous: the npm
        // stand-in delays before exiting non-zero, so the single-flight join
        // window is deterministic without any network access.
        var nodePath = Path.Combine(
            TestWorkspace.RepositoryRoot, "TestResults", "node22", "node-v22.23.1-win-x64", "node.exe");
        if (!File.Exists(nodePath))
            Assert.Inconclusive("Node 22 toolchain is not available under TestResults/node22.");

        var nodeDirectory = Path.Combine(paths.InstallDirectory, "tools", "node");
        Directory.CreateDirectory(Path.Combine(nodeDirectory, "node_modules", "npm", "bin"));
        File.Copy(nodePath, Path.Combine(nodeDirectory, "node.exe"));
        File.WriteAllText(
            Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js"),
            "setTimeout(() => { process.exitCode = 1; }, 250);");

        // Complete seed so the install reaches the npm step instead of
        // failing synchronously at the seed gate.
        Directory.CreateDirectory(paths.DshSeedDirectory);
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, ".npmrc"), "registry=https://registry.npmjs.org/");
    }

    private static void SeedCountingSlowNpm(RuntimePaths paths, string runsPath, int delayMs)
    {
        // Real Node 22 from TestResults with an npm stand-in that records each
        // run and then idles: cancellation (not natural exit) must be what
        // ends it, so the test can prove Stop cancels the only run instead of
        // waiting out (or starting) another.
        var nodePath = Path.Combine(
            TestWorkspace.RepositoryRoot, "TestResults", "node22", "node-v22.23.1-win-x64", "node.exe");
        if (!File.Exists(nodePath))
            Assert.Inconclusive("Node 22 toolchain is not available under TestResults/node22.");

        var nodeDirectory = Path.Combine(paths.InstallDirectory, "tools", "node");
        Directory.CreateDirectory(Path.Combine(nodeDirectory, "node_modules", "npm", "bin"));
        File.Copy(nodePath, Path.Combine(nodeDirectory, "node.exe"));
        File.WriteAllText(
            Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js"),
            $$"""
            require('fs').appendFileSync(process.env.PSX_DSH_TEST_RUNS, 'run\n');
            setTimeout(() => { process.exitCode = 1; }, {{delayMs}});
            """);
        Environment.SetEnvironmentVariable("PSX_DSH_TEST_RUNS", runsPath);

        // Complete seed so the retry reaches the npm step.
        Directory.CreateDirectory(paths.DshSeedDirectory);
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, ".npmrc"), "registry=https://registry.npmjs.org/");
    }

    private static void SeedFakeDshTree(string scratch, string version)
    {
        var packageDirectory = Path.Combine(scratch, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(packageDirectory, "lib"));
        File.WriteAllText(Path.Combine(packageDirectory, "lib", "bin.js"), "// fake dsh entry");
        File.WriteAllText(Path.Combine(packageDirectory, "package.json"), $"{{\"version\":\"{version}\"}}");
    }

    private sealed class ThrowingStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length || buffer.Length == 0)
                return 0;
            buffer[0] = data[_position++];
            if (_position >= data.Length)
                throw new IOException("simulated export stream failure");
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static System.Text.Json.JsonElement LastRuntimeStatus(RecordingAgentBridgeService bridge) =>
        bridge.Events.Last(message => message.GetProperty("type").GetString() == "dsh_runtime_status");

    private static (DshWebRuntime Runtime, RuntimePaths Paths) CreateRuntime(TestWorkspace workspace)
    {
        var locator = new RuntimeLocator(workspace.Path);
        var runtime = new DshWebRuntime(locator, Path.Combine(workspace.Path, "logs"));
        return (runtime, locator.Locate());
    }

    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length || buffer.Length == 0)
                return 0;
            buffer[0] = data[_position++];
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
