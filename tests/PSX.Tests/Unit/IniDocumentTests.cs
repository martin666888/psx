using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class IniDocumentTests
{
    [TestMethod]
    public void Parse_IgnoresCommentsAndUsesCaseInsensitiveLookups()
    {
        var document = IniDocument.Parse(
        [
            "; comment",
            "# another comment",
            "[Appearance]",
            "Theme = dark",
            "",
            "[Agent]",
            "Enabled = true"
        ], "settings.ini");

        Assert.AreEqual("dark", document.Get("appearance", "theme"));
        Assert.AreEqual("true", document.Get("AGENT", "ENABLED"));
        Assert.IsNull(document.Get("missing", "value"));
    }

    [TestMethod]
    public void Parse_RejectsDuplicateSection()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            IniDocument.Parse(["[Agent]", "Enabled=true", "[agent]"], "settings.ini"));

        StringAssert.Contains(exception.Message, "settings.ini:3");
        StringAssert.Contains(exception.Message, "duplicate section");
    }

    [TestMethod]
    public void Parse_RejectsDuplicateKey()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            IniDocument.Parse(["[Agent]", "Enabled=true", "enabled=false"], "settings.ini"));

        StringAssert.Contains(exception.Message, "settings.ini:3");
        StringAssert.Contains(exception.Message, "duplicate key");
    }

    [TestMethod]
    public void Parse_RejectsEntryBeforeSection()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            IniDocument.Parse(["Enabled=true"], "settings.ini"));

        StringAssert.Contains(exception.Message, "settings.ini:1");
        StringAssert.Contains(exception.Message, "malformed INI entry");
    }

    [TestMethod]
    public void Parse_RejectsEmptySectionName()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            IniDocument.Parse(["[]"], "settings.ini"));

        StringAssert.Contains(exception.Message, "settings.ini:1");
        StringAssert.Contains(exception.Message, "empty section name");
    }
}
