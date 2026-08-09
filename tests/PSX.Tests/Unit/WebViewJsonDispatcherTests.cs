using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class WebViewJsonDispatcherTests
{
    [TestMethod]
    public async Task SendAsync_OnOwningThread_PostsImmediately()
    {
        var ui = new RecordingUiDispatcher { HasAccess = true };
        var messages = new List<string>();
        using var sender = new WebViewJsonDispatcher(ui, messages.Add);

        var delivery = sender.SendAsync("one");

        Assert.IsTrue(delivery.IsCompletedSuccessfully);
        CollectionAssert.AreEqual(new[] { "one" }, messages);
        Assert.AreEqual(0, ui.PendingCount);
        await delivery;
    }

    [TestMethod]
    public async Task SendAsync_OffThread_CompletesAfterOrderedDelivery()
    {
        var ui = new RecordingUiDispatcher();
        var messages = new List<string>();
        using var sender = new WebViewJsonDispatcher(ui, messages.Add);

        var first = sender.SendAsync("one");
        var second = sender.SendAsync("two");

        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(2, ui.PendingCount);

        ui.ExecuteNext();
        await first;
        Assert.IsFalse(second.IsCompleted);
        CollectionAssert.AreEqual(new[] { "one" }, messages);

        ui.ExecuteNext();
        await second;
        CollectionAssert.AreEqual(new[] { "one", "two" }, messages);
    }

    [TestMethod]
    public async Task Dispose_DropsQueuedAndFutureMessages()
    {
        var ui = new RecordingUiDispatcher();
        var messages = new List<string>();
        var sender = new WebViewJsonDispatcher(ui, messages.Add);
        var queued = sender.SendAsync("queued");

        sender.Dispose();
        ui.ExecuteNext();

        await queued;
        await sender.SendAsync("late");
        Assert.IsEmpty(messages);
        Assert.AreEqual(0, ui.PendingCount);
    }

    [TestMethod]
    public async Task SendAsync_DuringDispatcherShutdown_IsSafeNoOp()
    {
        var ui = new RecordingUiDispatcher { HasShutdownStarted = true };
        var messages = new List<string>();
        using var sender = new WebViewJsonDispatcher(ui, messages.Add);

        await sender.SendAsync("ignored");

        Assert.IsEmpty(messages);
        Assert.AreEqual(0, ui.PendingCount);
    }

    [TestMethod]
    public async Task FireAndForgetBurst_QueuesWithoutBlockingProducer()
    {
        var ui = new RecordingUiDispatcher();
        var messages = new List<string>();
        using var sender = new WebViewJsonDispatcher(ui, messages.Add);
        var deliveries = Enumerable.Range(0, 100)
            .Select(index => sender.SendAsync(index.ToString()))
            .ToArray();

        Assert.IsTrue(deliveries.All(task => !task.IsCompleted));
        Assert.AreEqual(100, ui.PendingCount);

        ui.ExecuteAll();
        await Task.WhenAll(deliveries);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 100).Select(index => index.ToString()).ToArray(),
            messages);
    }

    [TestMethod]
    public void BridgeServices_UseTheSharedDispatcherInsteadOfDirectBeginInvoke()
    {
        var services = new[] { "Services/AgentBridgeService.cs", "Services/TerminalBridgeService.cs" };
        foreach (var relativePath in services)
        {
            var source = File.ReadAllText(Path.Combine(
                TestWorkspace.RepositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            StringAssert.Contains(source, "WebViewJsonDispatcher");
            Assert.IsFalse(
                source.Contains("BeginInvoke", StringComparison.Ordinal),
                $"{relativePath} must not enqueue WebView messages with BeginInvoke directly.");
        }
    }

    private sealed class RecordingUiDispatcher : IWebViewUiDispatcher
    {
        private readonly Queue<(Action Callback, TaskCompletionSource Completion)> _pending = new();

        public bool HasAccess { get; init; }

        public bool HasShutdownStarted { get; init; }

        public bool HasShutdownFinished { get; init; }

        public int PendingCount => _pending.Count;

        public bool CheckAccess() => HasAccess;

        public Task InvokeAsync(Action callback)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((callback, completion));
            return completion.Task;
        }

        public void ExecuteNext()
        {
            var (callback, completion) = _pending.Dequeue();
            try
            {
                callback();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }

        public void ExecuteAll()
        {
            while (_pending.Count > 0)
                ExecuteNext();
        }
    }
}
