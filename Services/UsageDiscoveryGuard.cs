using System.IO;

namespace PSX.Services;

/// <summary>
/// Traversal guard for local usage discovery: junctions and symlinks
/// (reparse points) are never descended into, so a linked tree cannot be
/// counted twice or loop the walk.
/// </summary>
internal static class UsageDiscoveryGuard
{
    public static bool ShouldDescend(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) == 0;
}
