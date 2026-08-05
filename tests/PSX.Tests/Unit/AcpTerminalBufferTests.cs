using System.Text;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpTerminalBufferTests
{
    [TestMethod]
    public void AppendBoundedUtf8_Ascii_RemovesTheOldestTextByByteLimit()
    {
        var output = new StringBuilder();

        var byteCount = AcpAgentSessionService.AppendBoundedUtf8(
            output,
            0,
            "abcdef".AsSpan(),
            4,
            out var truncated);

        Assert.IsTrue(truncated);
        Assert.AreEqual("cdef", output.ToString());
        Assert.AreEqual(4, byteCount);
    }

    [TestMethod]
    public void AppendBoundedUtf8_MultibyteText_NeverSplitsAUnicodeScalar()
    {
        var output = new StringBuilder();

        var byteCount = AcpAgentSessionService.AppendBoundedUtf8(
            output,
            0,
            "你好吗".AsSpan(),
            6,
            out var truncated);

        Assert.IsTrue(truncated);
        Assert.AreEqual("好吗", output.ToString());
        Assert.AreEqual(6, byteCount);
        Assert.AreEqual(byteCount, Encoding.UTF8.GetByteCount(output.ToString()));
    }

    [TestMethod]
    public void AppendBoundedUtf8_SurrogatePairSplitAcrossReads_ReconcilesRollingByteCount()
    {
        var output = new StringBuilder();
        var byteCount = AcpAgentSessionService.AppendBoundedUtf8(
            output,
            0,
            "\uD83D".AsSpan(),
            8,
            out var firstTruncated);

        byteCount = AcpAgentSessionService.AppendBoundedUtf8(
            output,
            byteCount,
            "\uDE00x".AsSpan(),
            5,
            out var secondTruncated);

        Assert.IsFalse(firstTruncated);
        Assert.IsFalse(secondTruncated);
        Assert.AreEqual("😀x", output.ToString());
        Assert.AreEqual(5, byteCount);
        Assert.AreEqual(byteCount, Encoding.UTF8.GetByteCount(output.ToString()));
    }

    [TestMethod]
    public void AppendBoundedUtf8_ZeroLimit_DropsEverythingAndReportsTruncation()
    {
        var output = new StringBuilder();

        var byteCount = AcpAgentSessionService.AppendBoundedUtf8(
            output,
            0,
            "😀中文".AsSpan(),
            0,
            out var truncated);

        Assert.IsTrue(truncated);
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(0, byteCount);
    }
}
