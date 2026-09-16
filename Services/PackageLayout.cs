using System.IO;

namespace PSX.Services;

/// <summary>Separates user-editable package files from bundled application resources.</summary>
internal sealed record PackageLayout(string PackageRoot, string ResourceRoot, bool IsCompact)
{
    public static PackageLayout Current => Resolve(AppContext.BaseDirectory);

    internal static PackageLayout Resolve(string resourceDirectory)
    {
        var root = Path.GetFullPath(resourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var compact = Path.GetFileName(root).Equals("app", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(root, "psx-compact-layout.json"))
            && File.ReadAllText(Path.Combine(root, "psx-compact-layout.json")).Trim() == "{\"version\":1}";
        return new(compact ? Path.GetDirectoryName(root)! : root, root, compact);
    }

    public string ConfigPath => Path.Combine(PackageRoot, "psx.ini");
    public string? WebViewDataDirectory => IsCompact ? Path.Combine(ResourceRoot, "state", "webview2") : null;
}
