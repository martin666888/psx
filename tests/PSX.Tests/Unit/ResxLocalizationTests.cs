using System.Xml.Linq;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ResxLocalizationTests
{
    private static readonly string[] Cultures = ["", ".zh-Hans", ".zh-Hant", ".ja"];

    private static string ResxPath(string cultureSuffix) =>
        Path.Combine(
            Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.Parent!.Parent!.FullName,
            "Properties", $"Strings{cultureSuffix}.resx");

    [TestMethod]
    public void Resx_AllFourLanguages_ShareTheSameKeySet()
    {
        var neutralKeys = ReadKeys(ResxPath(""));
        Assert.IsNotEmpty(neutralKeys, "neutral resource must not be empty");
        foreach (var suffix in Cultures.Skip(1))
        {
            var keys = ReadKeys(ResxPath(suffix));
            CollectionAssert.AreEquivalent(
                neutralKeys.ToList(), keys.ToList(),
                $"{suffix} key set must match the neutral resource");
        }
    }

    [TestMethod]
    public void Resx_NoEmptyValues_InAnyLanguage()
    {
        foreach (var suffix in Cultures)
        {
            foreach (var (name, value) in ReadEntries(ResxPath(suffix)))
                Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"{suffix}: {name} is empty");
        }
    }

    [TestMethod]
    public void Resx_FormatPlaceholders_MatchNeutralResource()
    {
        var neutral = ReadEntryMap(ResxPath(""));
        foreach (var suffix in Cultures.Skip(1))
        {
            var entries = ReadEntries(ResxPath(suffix));
            foreach (var (name, value) in entries)
            {
                var expected = CountPlaceholders(neutral[name]);
                var actual = CountPlaceholders(value);
                Assert.AreEqual(expected, actual, $"{suffix}: {name} placeholder count differs");
            }
        }
    }

    private static Dictionary<string, string> ReadEntryMap(string path) =>
        ReadEntries(path).ToDictionary(entry => entry.Name, entry => entry.Value);

    private static List<(string Name, string Value)> ReadEntries(string path)
    {
        var document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .Select(element => (element.Attribute("name")!.Value, element.Element("value")!.Value))
            .ToList();
    }

    private static HashSet<string> ReadKeys(string path) =>
        ReadEntries(path).Select(entry => entry.Name).ToHashSet();

    private static int CountPlaceholders(string value)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf('{', index)) >= 0)
        {
            count++;
            index++;
        }

        return count;
    }
}
