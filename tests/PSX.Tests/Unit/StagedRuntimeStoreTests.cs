using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class StagedRuntimeStoreTests
{
    [TestMethod]
    public void PromoteNextToCurrent_ReplacesCurrentAndWritesPointer()
    {
        using var workspace = TestWorkspace.Create(nameof(PromoteNextToCurrent_ReplacesCurrentAndWritesPointer));
        var current = Path.Combine(workspace.Path, "current");
        var next = Path.Combine(workspace.Path, "next");
        var pointer = Path.Combine(workspace.Path, "active.txt");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(next);
        File.WriteAllText(Path.Combine(current, "version.txt"), "old");
        File.WriteAllText(Path.Combine(next, "version.txt"), "new");
        var store = new StagedRuntimeStore("Test", _ => { });

        var promoted = store.PromoteNextToCurrent(workspace.Path, current, next, pointer);

        Assert.IsTrue(promoted);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(current, "version.txt")));
        Assert.IsFalse(Directory.Exists(next));
        Assert.AreEqual("current", File.ReadAllText(pointer));
    }

    [TestMethod]
    public void PromoteNextToCurrent_WithoutExistingCurrent_Succeeds()
    {
        using var workspace = TestWorkspace.Create(nameof(PromoteNextToCurrent_WithoutExistingCurrent_Succeeds));
        var current = Path.Combine(workspace.Path, "current");
        var next = Path.Combine(workspace.Path, "next");
        var pointer = Path.Combine(workspace.Path, "active.txt");
        Directory.CreateDirectory(next);
        File.WriteAllText(Path.Combine(next, "ready"), "yes");
        var store = new StagedRuntimeStore("Test", _ => { });

        var promoted = store.PromoteNextToCurrent(workspace.Path, current, next, pointer);

        Assert.IsTrue(promoted);
        Assert.IsTrue(File.Exists(Path.Combine(current, "ready")));
    }

    [TestMethod]
    public void PromoteNextToCurrent_MissingNext_RestoresExistingCurrent()
    {
        using var workspace = TestWorkspace.Create(nameof(PromoteNextToCurrent_MissingNext_RestoresExistingCurrent));
        var current = Path.Combine(workspace.Path, "current");
        var next = Path.Combine(workspace.Path, "missing-next");
        var pointer = Path.Combine(workspace.Path, "active.txt");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "ready"), "old");
        var store = new StagedRuntimeStore("Test", _ => { });

        var promoted = store.PromoteNextToCurrent(workspace.Path, current, next, pointer);

        Assert.IsFalse(promoted);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(current, "ready")));
        Assert.AreEqual("current", File.ReadAllText(pointer));
    }

    [TestMethod]
    public void PointerSaysNext_OnlyAcceptsExactNextToken()
    {
        using var workspace = TestWorkspace.Create(nameof(PointerSaysNext_OnlyAcceptsExactNextToken));
        var pointer = Path.Combine(workspace.Path, "active.txt");
        var store = new StagedRuntimeStore("Test", _ => { });
        Assert.IsTrue(store.TryWriteActivePointer(workspace.Path, pointer, "next"));
        Assert.IsTrue(store.PointerSaysNext(pointer));

        Assert.IsTrue(store.TryWriteActivePointer(workspace.Path, pointer, "current"));
        Assert.IsFalse(store.PointerSaysNext(pointer));
    }

    [TestMethod]
    public void NetworkClassification_RecognizesRegistryFailuresWithoutGuessingOtherErrors()
    {
        Assert.IsTrue(NpmRuntimeProcessRunner.LooksLikeNetworkError("npm ERR! code ENOTFOUND"));
        Assert.IsTrue(NpmRuntimeProcessRunner.LooksLikeNetworkError("npm ERR! code ETIMEDOUT"));
        Assert.IsFalse(NpmRuntimeProcessRunner.LooksLikeNetworkError("npm ERR! invalid package"));
        Assert.AreEqual(NpmRuntimeFailureKind.NotFound, NpmRuntimeProcessRunner.ClassifyFailure("npm ERR! code E404"));
        Assert.AreEqual(NpmRuntimeFailureKind.Integrity, NpmRuntimeProcessRunner.ClassifyFailure("npm ERR! code EINTEGRITY"));
    }
}
