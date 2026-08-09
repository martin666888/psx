using System.Text.Json;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpToolStateTrackerTests
{
    [TestMethod]
    public void ApplyUpdate_FirstCall_ReturnsStartAndCurrentSnapshots()
    {
        var tracker = new AcpToolStateTracker();
        using var document = JsonDocument.Parse(
            """{"toolCallId":"tool-1","kind":"read","rawInput":{"path":"a.txt"}}""");

        var result = tracker.ApplyUpdate(document.RootElement, AcpToolStateTracker.ReadStandardName);

        Assert.IsFalse(result.Suppressed);
        Assert.IsTrue(result.Started);
        Assert.IsTrue(result.SnapshotChanged);
        Assert.AreEqual("read", result.StartSnapshot!.Name);
        Assert.AreEqual("{\"path\":\"a.txt\"}", result.Snapshot!.Input);
        Assert.AreEqual("read a.txt", result.Snapshot.Summary);
    }

    [TestMethod]
    public void ApplyUpdate_TerminalChunks_AppendWithoutReplacingSnapshot()
    {
        var tracker = new AcpToolStateTracker();
        using var first = JsonDocument.Parse(
            """{"toolCallId":"tool-1","kind":"terminal","_meta":{"terminal_output":{"data":"one"}}}""");
        using var second = JsonDocument.Parse(
            """{"toolCallId":"tool-1","_meta":{"terminal_output":{"data":"two"}}}""");

        var firstResult = tracker.ApplyUpdate(first.RootElement, AcpToolStateTracker.ReadStandardName);
        var secondResult = tracker.ApplyUpdate(second.RootElement, AcpToolStateTracker.ReadStandardName);

        Assert.AreEqual("one", firstResult.TerminalDelta);
        Assert.AreEqual("two", secondResult.TerminalDelta);
        Assert.AreEqual("onetwo", secondResult.Snapshot!.Output);
        Assert.IsFalse(secondResult.SnapshotChanged);
    }

    [TestMethod]
    public void ApplyUpdate_PresentFields_ReplacePriorValues()
    {
        var tracker = new AcpToolStateTracker();
        using var first = JsonDocument.Parse(
            """{"toolCallId":"tool-1","kind":"read","rawInput":{"path":"old"},"rawOutput":"old output"}""");
        using var second = JsonDocument.Parse(
            """{"toolCallId":"tool-1","title":"Read new","rawInput":{"path":"new"},"content":[{"type":"text","text":"new output"}]}""");

        _ = tracker.ApplyUpdate(first.RootElement, AcpToolStateTracker.ReadStandardName);
        var result = tracker.ApplyUpdate(second.RootElement, AcpToolStateTracker.ReadStandardName);

        Assert.AreEqual("Read new", result.Snapshot!.Name);
        Assert.AreEqual("{\"path\":\"new\"}", result.Snapshot.Input);
        Assert.AreEqual("new output", result.Snapshot.Output);
    }

    [TestMethod]
    public void Complete_PendingParameterSnapshot_IsNotPersistedAsOutput()
    {
        var tracker = new AcpToolStateTracker();
        using var update = JsonDocument.Parse(
            """{"toolCallId":"tool-1","kind":"write","content":[{"type":"text","text":"{\"path\":\"pending\"}"}]}""");

        _ = tracker.ApplyUpdate(update.RootElement, AcpToolStateTracker.ReadStandardName);
        var completed = tracker.Complete("tool-1", "completed");

        Assert.IsNotNull(completed);
        Assert.AreEqual("", completed.Output);
    }

    [TestMethod]
    public void MarkDocumentDecision_SuppressesAndRemovesToolState()
    {
        var tracker = new AcpToolStateTracker();
        tracker.MarkDocumentDecision("tool-1");
        using var update = JsonDocument.Parse("""{"toolCallId":"tool-1","kind":"ask"}""");

        var result = tracker.ApplyUpdate(update.RootElement, AcpToolStateTracker.ReadStandardName);
        var reused = tracker.ApplyUpdate(update.RootElement, AcpToolStateTracker.ReadStandardName);

        Assert.IsTrue(result.Suppressed);
        Assert.IsNull(result.Snapshot);
        Assert.IsFalse(reused.Suppressed);
        Assert.IsTrue(reused.Started);
    }
}
