using Microsoft.Web.WebView2.Core;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class WebViewContextMenuPolicyTests
{
    [TestMethod]
    public void GetScope_EditableTarget_PreservesEditingSurface()
    {
        Assert.AreEqual(
            WebViewContextMenuScope.Editable,
            WebViewContextMenuPolicy.GetScope(true, CoreWebView2ContextMenuTargetKind.Page));
    }

    [TestMethod]
    public void GetScope_SelectedText_PreservesCopySurfaceOnly()
    {
        Assert.AreEqual(
            WebViewContextMenuScope.SelectedText,
            WebViewContextMenuPolicy.GetScope(false, CoreWebView2ContextMenuTargetKind.SelectedText));
        Assert.AreEqual(
            WebViewContextMenuScope.Suppressed,
            WebViewContextMenuPolicy.GetScope(false, CoreWebView2ContextMenuTargetKind.Page));
    }

    [TestMethod]
    public void IsAllowed_EditableTarget_AllowsEditingButNotBrowserCommands()
    {
        var allowed = new[]
        {
            "emoji", "undo", "redo", "cut", "copy", "paste",
            "pasteAndMatchStyle", "selectAll", "writingDirectionMenu"
        };
        foreach (var command in allowed)
            Assert.IsTrue(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.Editable, command), command);

        var blocked = new[]
        {
            "saveAs", "print", "reload", "viewSource", "inspectElement",
            "openLinkInNewWindow", "saveImageAs"
        };
        foreach (var command in blocked)
            Assert.IsFalse(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.Editable, command), command);
    }

    [TestMethod]
    public void IsAllowed_SelectedText_AllowsCopyAndNothingBrowserOwned()
    {
        Assert.IsTrue(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.SelectedText, "copy"));
        Assert.IsTrue(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.SelectedText, "selectAll"));
        Assert.IsFalse(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.SelectedText, "saveAs"));
        Assert.IsFalse(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.SelectedText, "print"));
        Assert.IsFalse(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.SelectedText, "openLinkInNewWindow"));
        Assert.IsFalse(WebViewContextMenuPolicy.IsAllowed(WebViewContextMenuScope.Suppressed, "copy"));
    }
}
