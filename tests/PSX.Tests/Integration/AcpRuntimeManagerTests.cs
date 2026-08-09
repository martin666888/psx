using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AcpRuntimeManagerTests
{
    [TestMethod]
    public async Task EnsureInstalled_FirstInstall_InstallsRegistryLatestAndActivatesCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_FirstInstall_InstallsRegistryLatestAndActivatesCurrent));
        fixture.Configure(Success("install", "acp-current", "1.2.3"));

        var result = await fixture.Manager.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.IsTrue(fixture.Manager.IsReady());
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.AreEqual("1.2.3", fixture.Manager.GetVersionInfo().CurrentAcpVersion);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.AcpCurrentDirectory, "package-lock.json")));
        var invocation = fixture.ReadInvocations().Single();
        Assert.AreEqual("install", invocation.Command);
        CollectionAssert.Contains(invocation.Arguments, "@agentclientprotocol/claude-agent-acp@latest");
        CollectionAssert.Contains(invocation.Arguments, "--include=optional");
        CollectionAssert.Contains(invocation.Arguments, "--no-audit");
        CollectionAssert.Contains(invocation.Arguments, "--no-fund");
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task EnsureInstalled_IncompleteRegistryResult_IsNotActivated(
        bool createAdapter,
        bool createClaude)
    {
        using var fixture = new FakeNpmFixture(
            $"{nameof(EnsureInstalled_IncompleteRegistryResult_IsNotActivated)}-{createAdapter}-{createClaude}");
        var scenario = Success("install", "acp-current", "1.2.3");
        scenario.CreateAdapter = createAdapter;
        scenario.CreateClaude = createClaude;
        fixture.Configure(scenario);

        var result = await fixture.Manager.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.IsFalse(fixture.Manager.IsReady());
        Assert.IsFalse(File.Exists(fixture.Paths.AcpActivePointerFile));
        Assert.HasCount(1, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task EnsureInstalled_ConcurrentCalls_RunNpmOnceAndSecondObservesReadyRuntime()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_ConcurrentCalls_RunNpmOnceAndSecondObservesReadyRuntime));
        var scenario = Success("install", "acp-current", "1.0.0");
        scenario.DelayMilliseconds = 250;
        fixture.Configure(scenario);

        var results = await Task.WhenAll(
            fixture.Manager.EnsureInstalledAsync(),
            fixture.Manager.EnsureInstalledAsync());

        Assert.AreEqual(1, results.Count(result => result.Kind == AcpRuntimeOperationKind.Success));
        Assert.AreEqual(1, results.Count(result => result.Kind == AcpRuntimeOperationKind.AlreadyReady));
        Assert.HasCount(1, fixture.ReadInvocations());
    }

    [TestMethod]
    public async Task EnsureInstalled_RegistryFailure_DoesNotActivateOrRetryFromSeed()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_RegistryFailure_DoesNotActivateOrRetryFromSeed));
        fixture.Configure(Failure("install", "acp-current", "registry failure"));

        var result = await fixture.Manager.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        var invocation = fixture.ReadInvocations().Single();
        Assert.AreEqual("install", invocation.Command);
        CollectionAssert.Contains(invocation.Arguments, "@agentclientprotocol/claude-agent-acp@latest");
        Assert.IsFalse(fixture.Manager.IsReady());
        Assert.IsFalse(File.Exists(fixture.Paths.AcpActivePointerFile));
    }

    [TestMethod]
    public async Task EnsureInstalled_NetworkFailure_DoesNotRunFallbackInstall()
    {
        using var fixture = new FakeNpmFixture(nameof(EnsureInstalled_NetworkFailure_DoesNotRunFallbackInstall));
        fixture.Configure(Failure("install", "acp-current", "getaddrinfo ENOTFOUND registry.npmjs.org"));

        var result = await fixture.Manager.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.NetworkUnavailable, result.Kind);
        Assert.HasCount(1, fixture.ReadInvocations());
        Assert.IsFalse(fixture.Manager.IsReady());
    }

    [TestMethod]
    public async Task EnsureInstalled_CancelledProcess_IsKilledAndReportedCancelled()
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var fixture = new FakeNpmFixture(
                $"{nameof(EnsureInstalled_CancelledProcess_IsKilledAndReportedCancelled)}-{iteration}");
            fixture.Configure(Hanging("install", "acp-current"));
            using var cancellation = new CancellationTokenSource();
            var operation = fixture.Manager.EnsureInstalledAsync(cancellationToken: cancellation.Token);
            await TestWorkspace.WaitUntilAsync(
                () => fixture.ReadInvocations().Count == 1,
                TimeSpan.FromSeconds(5),
                "Fake npm did not start.");
            var processId = fixture.ReadInvocations().Single().ProcessId;

            cancellation.Cancel();
            var result = await operation;

            Assert.AreEqual(AcpRuntimeOperationKind.Cancelled, result.Kind);
            Assert.IsFalse(
                FakeNpmFixture.IsProcessRunning(processId),
                "The cancellation result must not return until Fake npm has exited.");
            AssertDirectoryReleased(fixture.Paths.AcpCurrentDirectory);
        }
    }

    [TestMethod]
    public async Task EnsureInstalled_ProcessTimeout_IsKilledAndReportedFailed()
    {
        // The fake npm is a full .NET process; under parallel test load its CLR
        // startup alone can exceed a few hundred milliseconds. The timeout must
        // stay comfortably above that or the kill can land before the process
        // writes its invocation marker, making this assertion flaky.
        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var fixture = new FakeNpmFixture(
                $"{nameof(EnsureInstalled_ProcessTimeout_IsKilledAndReportedFailed)}-{iteration}",
                TimeSpan.FromSeconds(5));
            fixture.Configure(Hanging("install", "acp-current"));

            var result = await fixture.Manager.EnsureInstalledAsync();
            var invocations = fixture.ReadInvocations();

            Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
            StringAssert.Contains(result.Message, "timed out");
            Assert.HasCount(1, invocations);
            Assert.IsFalse(
                FakeNpmFixture.IsProcessRunning(invocations.Single().ProcessId),
                "The timeout result must not return until Fake npm has exited.");
            AssertDirectoryReleased(fixture.Paths.AcpCurrentDirectory);
        }
    }

    private static void AssertDirectoryReleased(string directory)
    {
        Assert.IsTrue(Directory.Exists(directory), $"Expected runtime directory '{directory}' to exist.");
        var probe = directory + ".released";
        Directory.Move(directory, probe);
        Directory.Move(probe, directory);
    }

    [TestMethod]
    public async Task Refresh_NewerCompleteRuntime_StagesNextWithoutChangingLiveProcessSpecThenPromotes()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NewerCompleteRuntime_StagesNextWithoutChangingLiveProcessSpecThenPromotes));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("current");
        fixture.Configure(Success("install", "acp-next", "2.0.0"));

        var refresh = await fixture.Manager.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, refresh.Kind);
        Assert.AreEqual("next", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        var version = fixture.Manager.GetVersionInfo();
        Assert.AreEqual("1.0.0", version.CurrentAcpVersion);
        Assert.AreEqual("2.0.0", version.PendingAcpVersion);
        Assert.IsTrue(version.HasPendingUpdate);
        Assert.AreEqual(fixture.Paths.AcpCurrentDirectory, fixture.Manager.CreateProcessSpec("work").WorkingDirectory);

        // The toolbar snapshot speaks Claude Code product versions; the ACP
        // adapter pair moves into the technical details string.
        var staged = fixture.Manager.GetVersionSnapshot();
        Assert.AreEqual("Claude Code", staged.ProductName);
        Assert.AreEqual("2.0.0-test", staged.CurrentVersion);
        Assert.AreEqual("2.1.0-test", staged.PendingVersion);
        Assert.IsTrue(staged.HasPendingUpdate);
        StringAssert.Contains(staged.TechnicalDetails!, "ACP adapter 1.0.0");
        StringAssert.Contains(staged.TechnicalDetails!, "2.0.0");

        Assert.IsTrue(await fixture.Manager.TryPromoteNextToCurrentAsync());
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.AreEqual("2.0.0", fixture.Manager.GetVersionInfo().CurrentAcpVersion);
        Assert.IsFalse(Directory.Exists(fixture.Paths.AcpNextDirectory));
        Assert.IsFalse(Directory.Exists(fixture.Paths.AcpCurrentDirectory + ".old"));
    }

    [TestMethod]
    public async Task Refresh_SameVersion_DiscardsStagingAndKeepsCurrentPointer()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_SameVersion_DiscardsStagingAndKeepsCurrentPointer));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("current");
        fixture.Configure(Success("install", "acp-next", "1.0.0"));

        var result = await fixture.Manager.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Success, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.IsFalse(Directory.Exists(fixture.Paths.AcpNextDirectory));
        Assert.AreEqual("1.0.0", fixture.Manager.GetVersionInfo().CurrentAcpVersion);
    }

    [TestMethod]
    public async Task Refresh_MissingAdapterAfterSuccessfulNpm_DoesNotActivateIncompleteStaging()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_MissingAdapterAfterSuccessfulNpm_DoesNotActivateIncompleteStaging));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("current");
        var scenario = Success("install", "acp-next", "2.0.0");
        scenario.CreateAdapter = false;
        fixture.Configure(scenario);

        var result = await fixture.Manager.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.AreEqual("1.0.0", fixture.Manager.GetVersionInfo().CurrentAcpVersion);
        Assert.IsFalse(fixture.Manager.GetVersionInfo().HasPendingUpdate);
    }

    [TestMethod]
    public async Task Refresh_MissingClaudeAfterSuccessfulNpm_DoesNotActivateIncompleteStaging()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_MissingClaudeAfterSuccessfulNpm_DoesNotActivateIncompleteStaging));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("current");
        var scenario = Success("install", "acp-next", "2.0.0");
        scenario.CreateClaude = false;
        fixture.Configure(scenario);

        var result = await fixture.Manager.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.AreEqual("1.0.0", fixture.Manager.GetVersionInfo().CurrentAcpVersion);
        Assert.IsFalse(fixture.Manager.GetVersionInfo().HasPendingUpdate);
    }

    [TestMethod]
    public async Task Refresh_NetworkFailure_LeavesCurrentRuntimeUsableAndNotPending()
    {
        using var fixture = new FakeNpmFixture(nameof(Refresh_NetworkFailure_LeavesCurrentRuntimeUsableAndNotPending));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("current");
        fixture.Configure(Failure("install", "acp-next", "network ETIMEDOUT"));

        var result = await fixture.Manager.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.NetworkUnavailable, result.Kind);
        Assert.IsTrue(fixture.Manager.IsReady());
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.IsFalse(fixture.Manager.GetVersionInfo().HasPendingUpdate);
    }

    [TestMethod]
    public async Task Promote_PointerReferencesMissingNext_RevertsPointerWithoutTouchingCurrent()
    {
        using var fixture = new FakeNpmFixture(nameof(Promote_PointerReferencesMissingNext_RevertsPointerWithoutTouchingCurrent));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("next");

        var promoted = await fixture.Manager.TryPromoteNextToCurrentAsync();

        Assert.IsFalse(promoted);
        Assert.AreEqual("current", File.ReadAllText(fixture.Paths.AcpActivePointerFile));
        Assert.AreEqual("1.0.0", fixture.Manager.GetVersionInfo().CurrentAcpVersion);
    }

    [TestMethod]
    public async Task RequestUpdate_ConcurrentRequests_RunNpmOnceAndShareTheResult()
    {
        using var fixture = new FakeNpmFixture(nameof(RequestUpdate_ConcurrentRequests_RunNpmOnceAndShareTheResult));
        fixture.CreateCompleteRuntime(fixture.Paths.AcpCurrentDirectory, "1.0.0");
        fixture.WritePointer("current");
        var scenario = Success("install", "acp-next", "2.0.0");
        scenario.DelayMilliseconds = 250;
        fixture.Configure(scenario);
        using var coordinator = new AgentRuntimeCoordinator(
            new FakeAgentProviderRegistry(new FakeAcpProvider(fixture.Manager)));

        var first = coordinator.RequestUpdateAsync(fixture.Manager);
        var second = coordinator.RequestUpdateAsync(fixture.Manager);
        var results = await Task.WhenAll(first, second);

        Assert.HasCount(1, fixture.ReadInvocations());
        Assert.IsTrue(results.All(result => result.Kind == AcpRuntimeOperationKind.Success));
        var snapshot = fixture.Manager.GetVersionSnapshot();
        Assert.IsTrue(snapshot.HasPendingUpdate);
        // Product (Claude Code) versions from the fixture manifests; the ACP
        // adapter 1.0.0 → 2.0.0 pair lives in TechnicalDetails only.
        Assert.AreEqual("2.1.0-test", snapshot.PendingVersion);
        Assert.AreEqual("2.0.0-test", snapshot.CurrentVersion);
        Assert.AreEqual("Claude Code", snapshot.ProductName);
        StringAssert.Contains(snapshot.TechnicalDetails!, "ACP adapter 1.0.0");
    }

    private static FakeNpmScenario Success(string command, string directory, string version) => new()
    {
        Command = command,
        WorkingDirectoryName = directory,
        AdapterVersion = version,
        ClaudeCodeVersion = "2.1.0-test"
    };

    private static FakeNpmScenario Failure(string command, string directory, string stderr) => new()
    {
        Command = command,
        WorkingDirectoryName = directory,
        ExitCode = 1,
        StandardError = stderr,
        CreateAdapter = false,
        CreateClaude = false
    };

    private static FakeNpmScenario Hanging(string command, string directory) => new()
    {
        Command = command,
        WorkingDirectoryName = directory,
        Hang = true,
        CreateAdapter = false,
        CreateClaude = false
    };
}
