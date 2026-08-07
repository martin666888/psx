using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class GitRootResolverTests
{
    [TestMethod]
    public void Resolve_SubdirectoryOfGitRepo_ReturnsTopLevel()
    {
        using var workspace = TestWorkspace.Create(nameof(Resolve_SubdirectoryOfGitRepo_ReturnsTopLevel));
        var root = Path.Combine(workspace.Path, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Path.Combine(root, "src", "feature");
        Directory.CreateDirectory(nested);

        Assert.AreEqual(root, GitRootResolver.Resolve(nested));
    }

    [TestMethod]
    public void Resolve_WorktreeGitFile_ReturnsTopLevel()
    {
        using var workspace = TestWorkspace.Create(nameof(Resolve_WorktreeGitFile_ReturnsTopLevel));
        var root = Path.Combine(workspace.Path, "worktree");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../repo/.git/worktrees/worktree");

        Assert.AreEqual(root, GitRootResolver.Resolve(root));
    }

    [TestMethod]
    public void Resolve_DirectoryWithoutOwnGit_WalksUpToEnclosingRepo()
    {
        using var workspace = TestWorkspace.Create(nameof(Resolve_DirectoryWithoutOwnGit_WalksUpToEnclosingRepo));
        var dir = Path.Combine(workspace.Path, "plain");
        Directory.CreateDirectory(dir);

        // Test workspaces live under the repository, so a git-less directory
        // walks up to the enclosing repo top-level instead of returning itself.
        var resolved = GitRootResolver.Resolve(dir);
        Assert.AreNotEqual(Path.GetFullPath(dir), resolved);
        Assert.AreEqual(GitRootResolver.Resolve(workspace.Path), resolved);

        // Trailing separators normalize away before the walk.
        Assert.AreEqual(resolved, GitRootResolver.Resolve(dir + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void Resolve_SameRootForSiblingSubdirectories()
    {
        using var workspace = TestWorkspace.Create(nameof(Resolve_SameRootForSiblingSubdirectories));
        var root = Path.Combine(workspace.Path, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var left = Path.Combine(root, "a");
        var right = Path.Combine(root, "b");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);

        Assert.AreEqual(
            GitRootResolver.Resolve(left),
            GitRootResolver.Resolve(right),
            StringComparer.OrdinalIgnoreCase);
    }
}
