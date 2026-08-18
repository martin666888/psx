using System.Diagnostics;
using System.Text.Json;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AcpJsonRpcTransportTests
{
    [TestMethod]
    public async Task SendRequestAsync_CorrelatesConcurrentResponses()
    {
        using var workspace = TestWorkspace.Create(nameof(SendRequestAsync_CorrelatesConcurrentResponses));
        using var transport = CreateTransport(workspace);

        var slow = transport.SendRequestAsync("test/echo", new { value = "slow", delayMs = 80 }, TimeSpan.FromSeconds(5));
        var fast = transport.SendRequestAsync("test/echo", new { value = "fast", delayMs = 5 }, TimeSpan.FromSeconds(5));
        var results = await Task.WhenAll(slow, fast);

        Assert.AreEqual("slow", results[0].GetProperty("parameters").GetProperty("value").GetString());
        Assert.AreEqual("fast", results[1].GetProperty("parameters").GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task AgentRequest_IsHandledWithoutBlockingTheResponseReader()
    {
        using var workspace = TestWorkspace.Create(nameof(AgentRequest_IsHandledWithoutBlockingTheResponseReader));
        string? receivedMethod = null;
        using var transport = CreateTransport(workspace, request =>
        {
            receivedMethod = request.GetProperty("method").GetString();
            return Task.FromResult<object?>(new { approved = true });
        });

        var result = await transport.SendRequestAsync("test/agent_request", new { }, TimeSpan.FromSeconds(5));

        Assert.AreEqual("client/confirm", receivedMethod);
        Assert.IsTrue(result.GetProperty("clientResult").GetProperty("approved").GetBoolean());
    }

    [TestMethod]
    public async Task Notification_IsDeliveredToTheConfiguredHandler()
    {
        using var workspace = TestWorkspace.Create(nameof(Notification_IsDeliveredToTheConfiguredHandler));
        JsonElement received = default;
        using var transport = CreateTransport(workspace, notificationHandler: notification =>
        {
            received = notification.Clone();
            return Task.CompletedTask;
        });

        await transport.SendRequestAsync("test/notify", new { }, TimeSpan.FromSeconds(5));
        await TestWorkspace.WaitUntilAsync(
            () => received.ValueKind == JsonValueKind.Object,
            TimeSpan.FromSeconds(2),
            "The Fake ACP notification was not delivered.");

        Assert.AreEqual("test/notification", received.GetProperty("method").GetString());
    }

    [TestMethod]
    public async Task TimedOutRequest_DoesNotPoisonTheTransport()
    {
        using var workspace = TestWorkspace.Create(nameof(TimedOutRequest_DoesNotPoisonTheTransport));
        using var transport = CreateTransport(workspace);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            transport.SendRequestAsync("test/hang", new { }, TimeSpan.FromMilliseconds(100)));
        var result = await transport.SendRequestAsync("test/echo", new { value = "still-running" }, TimeSpan.FromSeconds(5));

        Assert.AreEqual("still-running", result.GetProperty("parameters").GetProperty("value").GetString());
        Assert.IsTrue(transport.IsRunning);
    }

    [TestMethod]
    public async Task MalformedAgentOutput_DisconnectsAndFailsThePendingRequest()
    {
        using var workspace = TestWorkspace.Create(nameof(MalformedAgentOutput_DisconnectsAndFailsThePendingRequest));
        using var transport = CreateTransport(workspace);

        await Assert.ThrowsAsync<JsonException>(() =>
            transport.SendRequestAsync("test/malformed", new { }, TimeSpan.FromSeconds(5)));
        Assert.IsFalse(transport.IsRunning);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            transport.SendRequestAsync("test/echo", new { }, TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task Stderr_IsCapturedInTheRepositoryScopedTransportLog()
    {
        using var workspace = TestWorkspace.Create(nameof(Stderr_IsCapturedInTheRepositoryScopedTransportLog));
        var logPath = System.IO.Path.Combine(workspace.Path, "logs", "transport.log");
        using var transport = CreateTransport(workspace, logPath: logPath);

        await transport.SendRequestAsync("test/stderr", new { }, TimeSpan.FromSeconds(5));
        await TestWorkspace.WaitUntilAsync(
            () => File.Exists(logPath) && File.ReadAllText(logPath).Contains("fake-agent diagnostic", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2),
            "The transport did not capture stderr.");

        StringAssert.StartsWith(System.IO.Path.GetFullPath(logPath), System.IO.Path.GetFullPath(workspace.Path));
    }

    [TestMethod]
    public async Task Dispose_TerminatesTheFakeAgentProcess()
    {
        using var workspace = TestWorkspace.Create(nameof(Dispose_TerminatesTheFakeAgentProcess));
        var transport = CreateTransport(workspace);
        var result = await transport.SendRequestAsync("test/pid", new { }, TimeSpan.FromSeconds(5));
        var processId = result.GetProperty("processId").GetInt32();
        using var process = Process.GetProcessById(processId);

        transport.Dispose();

        await TestWorkspace.WaitUntilAsync(
            () => HasExited(process),
            TimeSpan.FromSeconds(5),
            "Disposing the ACP transport left the Fake ACP Agent running.");
    }

    [TestMethod]
    public async Task StalledAgentStdin_NeverBlocksTheCallingThreadOnEnqueue()
    {
        using var workspace = TestWorkspace.Create(nameof(StalledAgentStdin_NeverBlocksTheCallingThreadOnEnqueue));
        using var transport = CreateTransport(workspace);

        // Wedge the agent so it stops draining stdin but stays alive; the OS pipe
        // buffer then fills after a few KB.
        await transport.SendNotificationAsync("test/stall_stdin", new { });
        await Task.Delay(200);

        // Flood far past any pipe buffer. P0-1: the synchronous enqueue must stay
        // instant even though the background writer pump is now blocked on a full
        // pipe. The old synchronous writer flushed on the caller thread and would
        // block here forever, hanging the WPF UI thread on Stop/close.
        var payload = new string('x', 4096);
        var writes = new List<Task>();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 300; i++)
            writes.Add(transport.SendNotificationAsync("test/flood", new { i, payload }));
        stopwatch.Stop();

        Assert.IsTrue(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Enqueuing to a stalled child pipe must not block the caller; took {stopwatch.Elapsed}.");

        // Tear down and observe the queued writes so their faults are not left
        // unobserved once the pump is torn down.
        transport.Dispose();
        try { await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private static AcpJsonRpcTransport CreateTransport(
        TestWorkspace workspace,
        Func<JsonElement, Task<object?>>? requestHandler = null,
        Func<JsonElement, Task>? notificationHandler = null,
        string? logPath = null)
    {
        var transport = new AcpJsonRpcTransport(
            workspace.CreateTestAgentSpec(),
            logPath ?? System.IO.Path.Combine(workspace.Path, "logs", "transport.log"),
            requestHandler ?? (_ => Task.FromResult<object?>(new { })),
            notificationHandler ?? (_ => Task.CompletedTask));
        transport.Start();
        return transport;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
