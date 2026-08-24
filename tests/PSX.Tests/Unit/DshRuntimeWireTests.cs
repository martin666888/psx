using System.Net.Http.Headers;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DshRuntimeWireTests
{
    [TestMethod]
    [DataRow(DshRuntimeState.NotInstalled, "not_installed")]
    [DataRow(DshRuntimeState.Installing, "installing")]
    [DataRow(DshRuntimeState.Starting, "starting")]
    [DataRow(DshRuntimeState.Ready, "ready")]
    [DataRow(DshRuntimeState.Exited, "exited")]
    [DataRow(DshRuntimeState.Failed, "failed")]
    public void ToWireState_UsesExplicitMapping(DshRuntimeState state, string expected)
    {
        Assert.AreEqual(expected, DshWebRuntimeSupervisor.ToWireState(state));
    }

    [TestMethod]
    public void ToWireState_NotInstalled_IsNotEnumToLower()
    {
        Assert.AreEqual("notinstalled", DshRuntimeState.NotInstalled.ToString().ToLowerInvariant());
        Assert.AreEqual("not_installed", DshWebRuntimeSupervisor.ToWireState(DshRuntimeState.NotInstalled));
    }

    [TestMethod]
    [DataRow(DshUpdateState.Idle, "idle")]
    [DataRow(DshUpdateState.Checking, "checking")]
    [DataRow(DshUpdateState.UpToDate, "up_to_date")]
    [DataRow(DshUpdateState.Available, "available")]
    [DataRow(DshUpdateState.Updating, "updating")]
    [DataRow(DshUpdateState.Failed, "failed")]
    [DataRow(DshUpdateState.RequiresPsxUpdate, "requires_psx_update")]
    public void ToWireUpdateState_UsesExplicitMapping(DshUpdateState state, string expected)
    {
        Assert.AreEqual(expected, DshWebRuntimeSupervisor.ToWireUpdateState(state));
    }

    [TestMethod]
    public void DshErrorClass_WireSet_AddsLockAndCatalogClasses_DropsCrossCheck()
    {
        Assert.IsTrue(DshErrorClass.IsKnown(DshErrorClass.LockUnavailable));
        Assert.IsTrue(DshErrorClass.IsKnown(DshErrorClass.CatalogCorrupt));
        Assert.IsNull(typeof(DshErrorClass).GetField(
            "CrossCheckUnavailable", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        Assert.IsFalse(DshErrorClass.IsKnown("cross_check_unavailable"),
            "the removed class must no longer pass the wire whitelist");
        Assert.IsFalse(DshErrorClass.IsKnown("lock_unavailable" + "_x"),
            "unknown classes never pass the whitelist");
    }

    [TestMethod]
    public void ToWireLockSource_MapsBundled()
    {
        Assert.AreEqual("bundled", DshWebRuntime.ToWireLockSource(DshLockSourceKind.Bundled));
    }

    [TestMethod]
    [DataRow(DshUpdatePhase.Downloading, "downloading")]
    [DataRow(DshUpdatePhase.Validating, "validating")]
    [DataRow(DshUpdatePhase.Restarting, "restarting")]
    public void ToWireUpdatePhase_UsesExplicitMapping(DshUpdatePhase phase, string expected)
    {
        Assert.AreEqual(expected, DshWebRuntimeSupervisor.ToWireUpdatePhase(phase));
    }

    [TestMethod]
    [DataRow("0.1.0-rc.6", "0.1.0-rc.7", -1)]
    [DataRow("0.1.0-rc.10", "0.1.0", -1)]
    [DataRow("0.1.0", "0.1.0-rc.99", 1)]
    [DataRow("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [DataRow("1.0.0+build.1", "1.0.0+build.2", 0)]
    public void DshSemanticVersion_UsesSemverPrereleasePrecedence(
        string leftText,
        string rightText,
        int expectedSign)
    {
        Assert.IsTrue(DshSemanticVersion.TryParse(leftText, out var left));
        Assert.IsTrue(DshSemanticVersion.TryParse(rightText, out var right));
        Assert.AreEqual(expectedSign, Math.Sign(left.CompareTo(right)));
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_PrefersNextOverLatestWhenStrictlyNewer()
    {
        var stdout = """
            {
              "versions": ["0.1.1-rc.2", "0.1.1-rc.3", "0.1.1-rc.4"],
              "dist-tags": { "latest": "0.1.1-rc.3", "next": "0.1.1-rc.4" }
            }
            """;
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            stdout, "0.1.1-rc.3", out var catalog, out var error), error);
        Assert.HasCount(1, catalog);
        Assert.AreEqual("0.1.1-rc.4", catalog[0].Version);
        CollectionAssert.AreEqual(new[] { "next" }, catalog[0].Tags.ToArray());
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_ListsEveryStrictlyNewerVersionDescending()
    {
        var stdout = """
            {
              "versions": ["0.1.1-rc.2", "0.1.1-rc.3", "0.1.1-rc.4"],
              "dist-tags": { "latest": "0.1.1-rc.3", "next": "0.1.1-rc.4" }
            }
            """;
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            stdout, "0.1.1-rc.2", out var catalog, out var error), error);
        Assert.HasCount(2, catalog);
        Assert.AreEqual("0.1.1-rc.4", catalog[0].Version);
        Assert.AreEqual("0.1.1-rc.3", catalog[1].Version);
        CollectionAssert.AreEqual(new[] { "next" }, catalog[0].Tags.ToArray());
        CollectionAssert.AreEqual(new[] { "latest" }, catalog[1].Tags.ToArray());
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_AcceptsLegacyStringFixture()
    {
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            "\"0.1.1-rc.3\"", "0.1.1-rc.2", out var catalog, out var error), error);
        Assert.HasCount(1, catalog);
        Assert.AreEqual("0.1.1-rc.3", catalog[0].Version);
        Assert.IsEmpty(catalog[0].Tags);
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_DropsVersionsAtOrBelowCurrentAndBelowSeed()
    {
        var stdout = """
            {
              "versions": ["0.1.1-rc.1", "0.1.1-rc.2", "0.1.1-rc.3"],
              "dist-tags": { "latest": "0.1.1-rc.3" }
            }
            """;
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            stdout, "0.1.1-rc.3", out var catalog, out var error), error);
        Assert.IsEmpty(catalog);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("v0.1.0")]
    [DataRow("0.1")]
    [DataRow("0.1.0-rc..1")]
    [DataRow("01.1.0")]
    public void DshSemanticVersion_RejectsInvalidVersions(string value)
    {
        Assert.IsFalse(DshSemanticVersion.TryParse(value, out _));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:12345/")]
    [DataRow("http://127.0.0.1:12345")]
    [DataRow("HTTP://127.0.0.1:9")]
    public void TryAcceptReadyUrl_LoopbackHttp_Succeeds(string candidate)
    {
        Assert.IsTrue(DshWebRuntimeSupervisor.TryAcceptReadyUrl(candidate, out var url));
        Assert.AreEqual("127.0.0.1", url.Host);
        Assert.AreEqual(Uri.UriSchemeHttp, url.Scheme);
    }

    [TestMethod]
    [DataRow("https://127.0.0.1:12345/")]
    [DataRow("http://localhost:12345/")]
    [DataRow("http://0.0.0.0:12345/")]
    [DataRow("http://example.com/")]
    [DataRow("http://user@127.0.0.1:12345/")]
    [DataRow("ftp://127.0.0.1:12345/")]
    [DataRow("not a uri")]
    [DataRow("")]
    [DataRow(null)]
    public void TryAcceptReadyUrl_RejectsNonLoopbackHttp(string? candidate)
    {
        Assert.IsFalse(DshWebRuntimeSupervisor.TryAcceptReadyUrl(candidate, out _));
    }

    [TestMethod]
    public void ToFrameOrigin_StripsPathAndTrailingSlash()
    {
        var url = new Uri("http://127.0.0.1:4321/session?x=1");
        Assert.AreEqual("http://127.0.0.1:4321", DshWebRuntimeSupervisor.ToFrameOrigin(url));
        Assert.IsNull(DshWebRuntimeSupervisor.ToFrameOrigin(new Uri("https://127.0.0.1:4321/")));
        Assert.IsNull(DshWebRuntimeSupervisor.ToFrameOrigin(null));
    }

    [TestMethod]
    public void LooksLikeZip_AcceptsLocalFileAndEmptyArchiveMagic()
    {
        Assert.IsTrue(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
        Assert.IsTrue(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50, 0x4B, 0x05, 0x06 }));
        Assert.IsTrue(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50, 0x4B, 0x07, 0x08 }));
        Assert.IsFalse(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x50, 0x4B, 0x03 }));
        Assert.IsFalse(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x47, 0x5A, 0x03, 0x04 }));
        Assert.IsFalse(DshWebWorkspaceCoordinator.LooksLikeZip(new byte[] { 0x3C, 0x68, 0x74, 0x6D }));
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("application/zip", true)]
    [DataRow("application/x-zip-compressed", true)]
    [DataRow("application/octet-stream", true)]
    [DataRow("text/html", false)]
    [DataRow("application/json", false)]
    public void IsAllowedExportContentType_AcceptsZipFamilies(string? mediaType, bool expected)
    {
        var header = mediaType == null ? null : new MediaTypeHeaderValue(mediaType);
        Assert.AreEqual(expected, DshWebWorkspaceCoordinator.IsAllowedExportContentType(header));
    }
}
