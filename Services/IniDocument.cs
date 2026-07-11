using System.IO;
using System.Text;

namespace PSX.Services;

internal sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public static IniDocument Parse(IEnumerable<string> lines, string sourceName)
    {
        var document = new IniDocument();
        Dictionary<string, string>? current = null;
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (name.Length == 0)
                    throw new InvalidDataException($"{sourceName}:{lineNumber}: empty section name");
                if (!document._sections.TryAdd(name, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"{sourceName}:{lineNumber}: duplicate section [{name}]");
                current = document._sections[name];
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || current == null)
                throw new InvalidDataException($"{sourceName}:{lineNumber}: malformed INI entry");

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!current.TryAdd(key, value))
                throw new InvalidDataException($"{sourceName}:{lineNumber}: duplicate key {key}");
        }

        return document;
    }

    public bool TryGetSection(string name, out Dictionary<string, string> values) =>
        _sections.TryGetValue(name, out values!);

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
            ? value
            : null;
}
