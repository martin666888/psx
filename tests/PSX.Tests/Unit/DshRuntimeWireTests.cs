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
