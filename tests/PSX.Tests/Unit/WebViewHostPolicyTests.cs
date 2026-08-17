using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class WebViewHostPolicyTests
{
    [TestMethod]
    public void FrameOriginMap_ReplaceKimiOrigin_AtomicallyRevokesOldOrigin()
    {
        var map = new FrameOriginMap();
        Assert.IsTrue(map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"));
        Assert.IsTrue(map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4002"));

        var snapshot = map.Snapshot;
        Assert.HasCount(1, snapshot);
        Assert.AreEqual("http://127.0.0.1:4002", snapshot[WebViewHostPolicy.KimiWebFrameKind]);
        Assert.AreEqual((null, null), map.Classify("http://127.0.0.1:4001/"),
            "a replaced kimi origin is immediately revoked for frame navigation");
        Assert.AreEqual(
            (WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4002"),
            map.Classify("http://127.0.0.1:4002/api/v1/meta"));
    }

    [TestMethod]
    public void FrameOriginMap_ClearKimiOrigin_LeavesDshSlotUntouched()
    {
        var map = new FrameOriginMap();
        map.Set(WebViewHostPolicy.DshFrameKind, "http://127.0.0.1:5001");
        map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001");

        Assert.IsTrue(map.Set(WebViewHostPolicy.KimiWebFrameKind, null));

        var snapshot = map.Snapshot;
        Assert.HasCount(1, snapshot, "clearing kimi must not touch the dsh slot");
        Assert.AreEqual("http://127.0.0.1:5001", snapshot[WebViewHostPolicy.DshFrameKind]);
        Assert.AreEqual((null, null), map.Classify("http://127.0.0.1:4001/"));
        Assert.AreEqual(
            (WebViewHostPolicy.DshFrameKind, "http://127.0.0.1:5001"),
            map.Classify("http://127.0.0.1:5001/session"));
    }

    [TestMethod]
    public void FrameOriginMap_StaleInjectionReconfirmation_IsAbandonedAfterReplaceOrClear()
    {
        var map = new FrameOriginMap();
        map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001");
        Assert.IsTrue(map.IsCurrent(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"));

        // The runtime restarted on a new port: the frame that navigated to the
        // old port must not receive the injection script.
        map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4002");
        Assert.IsFalse(map.IsCurrent(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"),
            "a replaced slot abandons the stale injection");
        Assert.IsTrue(map.IsCurrent(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4002"));

        // The runtime stopped: every pending injection is abandoned.
        map.Set(WebViewHostPolicy.KimiWebFrameKind, null);
        Assert.IsFalse(map.IsCurrent(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4002"),
            "a cleared slot abandons the injection");
    }

    [TestMethod]
    public void FrameOriginMap_SameValueAndUnknownClear_AreNoOps()
    {
        var map = new FrameOriginMap();
        Assert.IsTrue(map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"));
        Assert.IsFalse(map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"),
            "an unchanged slot never swaps the snapshot");
        Assert.IsTrue(map.Set(WebViewHostPolicy.KimiWebFrameKind, null),
            "clearing a present slot changes the snapshot");
        Assert.IsFalse(map.Set(WebViewHostPolicy.KimiWebFrameKind, null),
            "clearing an already-empty slot is a no-op");
        Assert.IsFalse(map.Set(WebViewHostPolicy.DshFrameKind, null),
            "clearing an absent slot is a no-op");
    }

    [TestMethod]
    public void FrameOriginMap_Classify_MatchesExactCurrentOriginOnly()
    {
        var map = new FrameOriginMap();
        map.Set(WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001");

        Assert.AreEqual(
            (WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"),
            map.Classify("http://127.0.0.1:4001/"));
        Assert.AreEqual(
            (WebViewHostPolicy.KimiWebFrameKind, "http://127.0.0.1:4001"),
            map.Classify("http://127.0.0.1:4001/api/v1/healthz"));
        Assert.AreEqual((null, null), map.Classify("http://127.0.0.1:4002/"));
        Assert.AreEqual((null, null), map.Classify("about:blank"));
        Assert.AreEqual((null, null), map.Classify(null));
    }
}
