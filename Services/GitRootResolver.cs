using System.Collections.Concurrent;
using System.IO;

namespace PSX.Services;

/// <summary>
/// Resolves a working directory to its git top-level (worktree root) by
/// walking up for a <c>.git</c> entry (directory or worktree/submodule file).
/// Comparison is on the normalized root, never raw cwd strings — subdirectories,
/// trailing separators and Windows casing would otherwise misfire. Results are
/// cached per normalized directory; misses (non-git dirs) resolve to the
/// directory itself.
/// </summary>
internal static class GitRootResolver
{
    private static readonly ConcurrentDictionary<string, string> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return string.Empty;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(workingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return workingDirectory.Trim();
        }

        return Cache.GetOrAdd(normalized, ResolveUncached);
    }

    private static string ResolveUncached(string directory)
    {
        var current = directory;
        while (true)
        {
            var gitEntry = Path.Combine(current, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return current;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return directory;
            current = parent;
        }
    }

    /// <summary>Test hook: cached resolutions would outlive fixture dirs.</summary>
    internal static void ClearCache() => Cache.Clear();
}
