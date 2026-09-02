using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
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
              "versions": ["0.1.2-alpha.2", "0.1.2-alpha.6", "0.1.2-alpha.7"],
              "dist-tags": { "latest": "0.1.2-alpha.6", "next": "0.1.2-alpha.7" }
            }
            """;
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            stdout, "0.1.2-alpha.6", out var catalog, out var error), error);
        Assert.HasCount(1, catalog);
        Assert.AreEqual("0.1.2-alpha.7", catalog[0].Version);
        CollectionAssert.AreEqual(new[] { "next" }, catalog[0].Tags.ToArray());
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_ListsEveryStrictlyNewerVersionDescending()
    {
        var stdout = """
            {
              "versions": ["0.1.2-alpha.2", "0.1.2-alpha.6", "0.1.2-alpha.7"],
              "dist-tags": { "latest": "0.1.2-alpha.6", "next": "0.1.2-alpha.7" }
            }
            """;
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            stdout, "0.1.2-alpha.2", out var catalog, out var error), error);
        Assert.HasCount(2, catalog);
        Assert.AreEqual("0.1.2-alpha.7", catalog[0].Version);
        Assert.AreEqual("0.1.2-alpha.6", catalog[1].Version);
        CollectionAssert.AreEqual(new[] { "next" }, catalog[0].Tags.ToArray());
        CollectionAssert.AreEqual(new[] { "latest" }, catalog[1].Tags.ToArray());
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_AcceptsLegacyStringFixture()
    {
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            "\"0.1.2-alpha.6\"", "0.1.2-alpha.2", out var catalog, out var error), error);
        Assert.HasCount(1, catalog);
        Assert.AreEqual("0.1.2-alpha.6", catalog[0].Version);
        Assert.IsEmpty(catalog[0].Tags);
    }

    [TestMethod]
    public void TryBuildUpdateCatalog_DropsVersionsAtOrBelowCurrentAndBelowSeed()
    {
        var stdout = """
            {
              "versions": ["0.1.2-alpha.1", "0.1.2-alpha.2", "0.1.2-alpha.6"],
              "dist-tags": { "latest": "0.1.2-alpha.6" }
            }
            """;
        Assert.IsTrue(DshWebRuntime.TryBuildUpdateCatalog(
            stdout, "0.1.2-alpha.6", out var catalog, out var error), error);
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
    [DataRow("http://127.0.0.1:12345/?token=abcdefgh")]
    [DataRow("http://127.0.0.1:9/?token=abc.def~ghi-jkl")]
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
    [DataRow("http://127.0.0.1:12345/?token=short")]
    [DataRow("http://127.0.0.1:12345/?token=abcdefgh&extra=1")]
    [DataRow("http://127.0.0.1:12345/?other=abcdefgh")]
    [DataRow("http://127.0.0.1:12345/#token=abcdefgh")]
    [DataRow("http://127.0.0.1:12345/session")]
    [DataRow("http://127.0.0.1:12345/?token=abcd!efgh")]
    [DataRow("http://127.0.0.1:12345/?token=abcdefgh+")]
    public void TryAcceptReadyUrl_RejectsNonLoopbackHttp(string? candidate)
    {
        Assert.IsFalse(DshWebRuntimeSupervisor.TryAcceptReadyUrl(candidate, out _));
    }

    [TestMethod]
    public void ToFrameOrigin_StripsPathAndTrailingSlash()
    {
        var url = new Uri("http://127.0.0.1:4321/session?x=1");
        Assert.AreEqual("http://127.0.0.1:4321", DshWebRuntimeSupervisor.ToFrameOrigin(url));
        Assert.AreEqual(
            "http://127.0.0.1:4321",
            DshWebRuntimeSupervisor.ToFrameOrigin(new Uri("http://127.0.0.1:4321/?token=abcdefgh")));
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

    [TestMethod]
    public async Task HealthCheck_DoesNotFollowRedirectToTokenUrl()
    {
        using var server = TokenRedirectRootServer.Start();
        var ready = new Uri($"{server.Origin}/?token=abcdefgh");
        var ok = await DshWebRuntimeSupervisor.HealthCheckAsync(ready);
        Assert.IsFalse(ok, "a 3xx from the clean root must not count as Ready");
        Assert.AreEqual(0, server.TokenHits, "HealthCheck must not follow a redirect onto the token URL");
        Assert.IsGreaterThanOrEqualTo(1, server.RootHits);
    }

    private sealed class TokenRedirectRootServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string Origin { get; }
        public int TokenHits;
        public int RootHits;

        private TokenRedirectRootServer(int port)
        {
            Origin = $"http://127.0.0.1:{port}";
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public static TokenRedirectRootServer Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new TokenRedirectRootServer(port);
            server._listener.Start();
            _ = Task.Run(server.ServeAsync);
            return server;
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(client, _cts.Token));
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[1024];
                    var builder = new StringBuilder();
                    while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (read == 0) return;
                        builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (builder.Length > 4096) return;
                    }

                    var requestLine = builder.ToString().Split("\r\n")[0];
                    var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                    if (target.Contains("token=", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref TokenHits);
                        var html = Encoding.UTF8.GetBytes("<!doctype html><title>DeepSeek Harness</title>");
                        var ok = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(ok, token).ConfigureAwait(false);
                        await stream.WriteAsync(html, token).ConfigureAwait(false);
                        return;
                    }

                    Interlocked.Increment(ref RootHits);
                    var body = Encoding.ASCII.GetBytes("redirect");
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 302 Found\r\nLocation: /?token=abcdefgh\r\nContent-Length: "
                        + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, token).ConfigureAwait(false);
                    await stream.WriteAsync(body, token).ConfigureAwait(false);
                }
                catch
                {
                    // probe listener
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
