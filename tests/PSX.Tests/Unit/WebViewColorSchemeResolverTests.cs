using Microsoft.Web.WebView2.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
public sealed class WebViewColorSchemeResolverTests
{
    [TestMethod]
    [DataRow("#0a0a0a", CoreWebView2PreferredColorScheme.Dark)]
    [DataRow("#1b1d20", CoreWebView2PreferredColorScheme.Dark)]
    [DataRow("#ffffff", CoreWebView2PreferredColorScheme.Light)]
    [DataRow("#f7f3e8ff", CoreWebView2PreferredColorScheme.Light)]
    [DataRow("invalid", CoreWebView2PreferredColorScheme.Auto)]
    [DataRow(null, CoreWebView2PreferredColorScheme.Auto)]
    public void Resolve_MapsThemeBackgroundToBrowserPreference(
        string? background,
        CoreWebView2PreferredColorScheme expected)
    {
        Assert.AreEqual(expected, WebViewColorSchemeResolver.Resolve(background));
    }
}
