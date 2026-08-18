using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentProfileStoreTests
{
    // A 1x1 transparent PNG (valid 8-byte signature + IHDR/IDAT/IEND).
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [TestMethod]
    public void GetProfile_NoFile_FallsBackToUserNameWithZeroRevision()
    {
        using var workspace = TestWorkspace.Create(nameof(GetProfile_NoFile_FallsBackToUserNameWithZeroRevision));
        var store = new AgentProfileStore(workspace.Path);

        var profile = store.GetProfile();

        Assert.IsFalse(string.IsNullOrWhiteSpace(profile.DisplayName));
        Assert.AreEqual(0, profile.Revision);
        Assert.IsNull(profile.AvatarDataUrl);
    }

    [TestMethod]
    public void SetDisplayName_TrimsAndClampsAndBumpsRevision()
    {
        using var workspace = TestWorkspace.Create(nameof(SetDisplayName_TrimsAndClampsAndBumpsRevision));
        var store = new AgentProfileStore(workspace.Path);

        var result = store.SetDisplayName("  Codey  ");
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Codey", result.Profile.DisplayName);
        Assert.AreEqual(1, result.Profile.Revision);

        var longName = new string('x', AgentProfileStore.MaxDisplayNameLength + 20);
        var clamped = store.SetDisplayName(longName);
        Assert.AreEqual(AgentProfileStore.MaxDisplayNameLength, clamped.Profile.DisplayName.Length);
        Assert.AreEqual(2, clamped.Profile.Revision);
    }

    [TestMethod]
    public void SetDisplayName_Empty_FallsBackToDefault()
    {
        using var workspace = TestWorkspace.Create(nameof(SetDisplayName_Empty_FallsBackToDefault));
        var store = new AgentProfileStore(workspace.Path);

        var result = store.SetDisplayName("   ");

        Assert.IsTrue(result.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Profile.DisplayName));
    }

    [TestMethod]
    public void SetAvatar_ValidPng_PersistsAsDataUrl()
    {
        using var workspace = TestWorkspace.Create(nameof(SetAvatar_ValidPng_PersistsAsDataUrl));
        var store = new AgentProfileStore(workspace.Path);

        var result = store.SetAvatar(TinyPng);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Profile.AvatarDataUrl);
        StringAssert.StartsWith(result.Profile.AvatarDataUrl!, "data:image/png;base64,");
        Assert.AreEqual(result.Profile.AvatarDataUrl, store.GetProfile().AvatarDataUrl);
    }

    [TestMethod]
    public void SetAvatar_NonPng_Rejected()
    {
        using var workspace = TestWorkspace.Create(nameof(SetAvatar_NonPng_Rejected));
        var store = new AgentProfileStore(workspace.Path);

        var result = store.SetAvatar([0x42, 0x4D, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06]);

        Assert.IsFalse(result.Success);
        Assert.IsNull(store.GetProfile().AvatarDataUrl);
    }

    [TestMethod]
    public void SetAvatar_TooLarge_Rejected()
    {
        using var workspace = TestWorkspace.Create(nameof(SetAvatar_TooLarge_Rejected));
        var store = new AgentProfileStore(workspace.Path);
        var oversize = new byte[AgentProfileStore.MaxAvatarBytes + 1];
        Array.Copy(TinyPng, oversize, TinyPng.Length);

        var result = store.SetAvatar(oversize);

        Assert.IsFalse(result.Success);
        Assert.IsNull(store.GetProfile().AvatarDataUrl);
    }

    [TestMethod]
    public void GetProfile_CorruptJson_FallsBackToDefault()
    {
        using var workspace = TestWorkspace.Create(nameof(GetProfile_CorruptJson_FallsBackToDefault));
        System.IO.File.WriteAllText(System.IO.Path.Combine(workspace.Path, "profile.json"), "{ not json");
        var store = new AgentProfileStore(workspace.Path);

        var profile = store.GetProfile();

        Assert.IsFalse(string.IsNullOrWhiteSpace(profile.DisplayName));
    }

    [TestMethod]
    public void Revision_IsMonotonicAcrossReopen()
    {
        using var workspace = TestWorkspace.Create(nameof(Revision_IsMonotonicAcrossReopen));
        var store = new AgentProfileStore(workspace.Path);
        store.SetDisplayName("A");
        store.SetDisplayName("B");

        var reopened = new AgentProfileStore(workspace.Path);
        var next = reopened.SetDisplayName("C");

        Assert.AreEqual(3, next.Profile.Revision);
    }
}
