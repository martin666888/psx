using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class WebViewNavigationPolicyTests
{
    [TestMethod]
    [DataRow("https://psx.local/index.html")]
    [DataRow("https://psx.local/app/index.html")]
    [DataRow("https://PSX.LOCAL/index.html?tab=agent#plan")]
    [DataRow("https://psx-attachments.local/thread/image.png")]
    public void Classify_ExactApplicationOrigins_AreInternal(string value)
    {
        Assert.AreEqual(WebViewNavigationTarget.Internal, WebViewNavigationPolicy.Classify(value));
    }

    [TestMethod]
    [DataRow("https://example.com/path")]
    [DataRow("http://example.com/")]
    [DataRow("mailto:test@example.com")]
    public void Classify_SafeSystemUris_AreExternal(string value)
    {
        Assert.AreEqual(WebViewNavigationTarget.External, WebViewNavigationPolicy.Classify(value));
    }

    [TestMethod]
    [DataRow("https://psx.local.evil/index.html")]
    [DataRow("http://psx.local/index.html")]
    [DataRow("http://psx-attachments.local/image.png")]
    [DataRow("https://psx.local:8443/index.html")]
    [DataRow("https://user@psx.local/index.html")]
    [DataRow("https://example.com:8443/")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,hello")]
    [DataRow("file:///C:/Windows/System32")]
    [DataRow("custom://open")]
    [DataRow("not a uri")]
    public void Classify_UntrustedUris_AreBlocked(string value)
    {
        Assert.AreEqual(WebViewNavigationTarget.Blocked, WebViewNavigationPolicy.Classify(value));
    }
}
