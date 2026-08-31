using PSX.Helpers;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class HwndClipRegionTests
{
    [TestMethod]
    public void IntersectHole_NoOverlap_ReturnsNull()
    {
        var hole = HwndClipRegion.IntersectHole(
            overlayLeft: 100, overlayTop: 100, overlayWidth: 400, overlayHeight: 400,
            holeLeft: 0, holeTop: 0, holeWidth: 50, holeHeight: 50,
            dpiScaleX: 1, dpiScaleY: 1);
        Assert.IsNull(hole);
    }

    [TestMethod]
    public void IntersectHole_FullyInside_IsLocalPixels()
    {
        var hole = HwndClipRegion.IntersectHole(
            overlayLeft: 40, overlayTop: 80, overlayWidth: 800, overlayHeight: 600,
            holeLeft: 60, holeTop: 100, holeWidth: 260, holeHeight: 320,
            dpiScaleX: 1, dpiScaleY: 1);
        Assert.IsNotNull(hole);
        Assert.AreEqual(20, hole.Value.X);
        Assert.AreEqual(20, hole.Value.Y);
        Assert.AreEqual(260, hole.Value.Width);
        Assert.AreEqual(320, hole.Value.Height);
    }

    [TestMethod]
    public void IntersectHole_DpiScale_RoundsAwayFromZero()
    {
        var hole = HwndClipRegion.IntersectHole(
            overlayLeft: 0, overlayTop: 0, overlayWidth: 200, overlayHeight: 200,
            holeLeft: 10, holeTop: 10, holeWidth: 40, holeHeight: 40,
            dpiScaleX: 1.5, dpiScaleY: 1.5);
        Assert.IsNotNull(hole);
        Assert.AreEqual(15, hole.Value.X);
        Assert.AreEqual(15, hole.Value.Y);
        Assert.AreEqual(60, hole.Value.Width);
        Assert.AreEqual(60, hole.Value.Height);
    }

    [TestMethod]
    public void IntersectHole_OverlappingEdge_ClampsToOverlay()
    {
        var hole = HwndClipRegion.IntersectHole(
            overlayLeft: 100, overlayTop: 100, overlayWidth: 200, overlayHeight: 200,
            holeLeft: 50, holeTop: 50, holeWidth: 80, holeHeight: 80,
            dpiScaleX: 1, dpiScaleY: 1);
        Assert.IsNotNull(hole);
        Assert.AreEqual(0, hole.Value.X);
        Assert.AreEqual(0, hole.Value.Y);
        Assert.AreEqual(30, hole.Value.Width);
        Assert.AreEqual(30, hole.Value.Height);
    }

    [TestMethod]
    public void CornerEllipsePx_MatchesCssRadiusAsDiameter()
    {
        Assert.AreEqual(0, HwndClipRegion.CornerEllipsePx(300, 360, 0));
        Assert.AreEqual(68, HwndClipRegion.CornerEllipsePx(300, 360, 34));
        Assert.AreEqual(100, HwndClipRegion.CornerEllipsePx(100, 400, 80),
            "radius cannot exceed half the shorter side");
    }

    [TestMethod]
    public void Clear_Null_DoesNotThrow()
    {
        HwndClipRegion.Clear(null);
    }
}
