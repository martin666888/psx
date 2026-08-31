using PSX.Helpers;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class HwndZOrderTests
{
    [TestMethod]
    public void TryGetHandle_Null_ReturnsZero()
    {
        Assert.AreEqual(IntPtr.Zero, HwndZOrder.TryGetHandle(null));
    }

    [TestMethod]
    public void BringToFront_Null_DoesNotThrow()
    {
        HwndZOrder.BringToFront(null);
    }
}
