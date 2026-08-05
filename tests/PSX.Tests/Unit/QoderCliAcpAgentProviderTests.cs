using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QoderCliAcpAgentProviderTests
{
    private const string SeedVersion = QoderCliAcpRuntime.SeededPackageVersion;

    [TestMethod]
    public void Descriptor_MatchesManagedQoderContract()
    {
        using var fixture = new FakeNpmFixture(nameof(Descriptor_MatchesManagedQoderContract));
        var provider = CreateProvider(fixture);

        Assert.AreEqual("acp-qoder", provider.Descriptor.Key);
        Assert.AreEqual("Qoder CLI", provider.Descriptor.DisplayName);
        Assert.AreEqual("Qoder", provider.Descriptor.AssistantName);
        Assert.AreEqual("qoder", provider.Descriptor.IconKey);
        Assert.HasCount(0, provider.Descriptor.LegacyKeys);
        Assert.IsNull(provider.UsageSource);
    }

    [TestMethod]
    public void Runtime_IsManagedWithSelfUpdate()
    {
        using var fixture = new FakeNpmFixture(nameof(Runtime_IsManagedWithSelfUpdate));
        var provider = CreateProvider(fixture);

        Assert.IsTrue(provider.Runtime.SupportsSelfUpdate);
        Assert.IsFalse(provider.Runtime.IsReady());
    }

    [TestMethod]
    public void CreateNewSessionParameters_CarriesCwdAndEmptyMcpServers()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateNewSessionParameters_CarriesCwdAndEmptyMcpServers));
        var provider = CreateProvider(fixture);

        using var json = JsonSerializer.SerializeToDocument(
            provider.CreateNewSessionParameters("C:/projects/app"));
        var root = json.RootElement;

        Assert.AreEqual("C:/projects/app", root.GetProperty("cwd").GetString());
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("mcpServers").ValueKind);
        Assert.AreEqual(0, root.GetProperty("mcpServers").GetArrayLength());
    }

    [TestMethod]
    public void CreateRestoreSessionParameters_CarriesSessionIdCwdAndEmptyMcpServers()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateRestoreSessionParameters_CarriesSessionIdCwdAndEmptyMcpServers));
        var provider = CreateProvider(fixture);

        using var json = JsonSerializer.SerializeToDocument(
            provider.CreateRestoreSessionParameters("session-123", "C:/projects/app"));
        var root = json.RootElement;

        Assert.AreEqual("session-123", root.GetProperty("sessionId").GetString());
        Assert.AreEqual("C:/projects/app", root.GetProperty("cwd").GetString());
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("mcpServers").ValueKind);
        Assert.AreEqual(0, root.GetProperty("mcpServers").GetArrayLength());
    }

    [TestMethod]
    public void IsCommandVisible_ShowsEveryCommandInV1()
    {
        using var fixture = new FakeNpmFixture(nameof(IsCommandVisible_ShowsEveryCommandInV1));
        var provider = CreateProvider(fixture);

        Assert.IsTrue(provider.IsCommandVisible("anything"));
        Assert.IsTrue(provider.IsCommandVisible("/terminal"));
    }

    [TestMethod]
    public void CreateNativeTerminalProfile_UsesManagedNodeEntryWithoutAcp()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateNativeTerminalProfile_UsesManagedNodeEntryWithoutAcp));
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        var provider = CreateProvider(fixture);

        var profile = provider.CreateNativeTerminalProfile("C:/projects/app", "session-9");

        Assert.IsNotNull(profile);
        StringAssert.Contains(profile!.Arguments, fixture.Paths.PortableNodePath!);
        StringAssert.Contains(profile.Arguments, "qodercli.js");
        Assert.IsFalse(profile.Arguments.Contains("--resume", StringComparison.Ordinal));
        Assert.IsFalse(profile.Arguments.Contains("--acp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateNativeTerminalProfile_IncompleteInstall_ReturnsNull()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateNativeTerminalProfile_IncompleteInstall_ReturnsNull));
        var provider = CreateProvider(fixture);

        Assert.IsNull(provider.CreateNativeTerminalProfile("C:/projects/app", "session-9"));
    }

    [TestMethod]
    public void CreateLoginTerminalProfile_UsesManagedEntryPlusLogin()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateLoginTerminalProfile_UsesManagedEntryPlusLogin));
        fixture.InstallQoderSeed();
        fixture.CreateQoderInstall(fixture.Paths.QoderCurrentDirectory, SeedVersion);
        var provider = CreateProvider(fixture);

        var profile = provider.CreateLoginTerminalProfile("C:/projects/app");

        Assert.IsNotNull(profile);
        StringAssert.Contains(profile!.Arguments, fixture.Paths.PortableNodePath!);
        StringAssert.Contains(profile.Arguments, "qodercli.js");
        StringAssert.Contains(profile.Arguments, "'login'");
        Assert.IsFalse(profile.Arguments.Contains("--acp", StringComparison.Ordinal));
        Assert.IsFalse(profile.Arguments.Contains("--login", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateLoginTerminalProfile_IncompleteInstall_ReturnsNull()
    {
        using var fixture = new FakeNpmFixture(nameof(CreateLoginTerminalProfile_IncompleteInstall_ReturnsNull));
        var provider = CreateProvider(fixture);

        Assert.IsNull(provider.CreateLoginTerminalProfile("C:/projects/app"));
    }

    [TestMethod]
    public void NewSessionPolicy_AllowsSlowUnauthenticatedStartup()
    {
        using var fixture = new FakeNpmFixture(nameof(NewSessionPolicy_AllowsSlowUnauthenticatedStartup));
        var provider = CreateProvider(fixture);

        Assert.IsTrue(provider.NewSessionTimeout >= TimeSpan.FromSeconds(90));
        Assert.IsTrue(provider.TreatNewSessionTimeoutAsAuthRequired);
    }

    [TestMethod]
    public void ClientCapabilities_AdvertisesFullReverseFilesystemBridge()
    {
        using var fixture = new FakeNpmFixture(nameof(ClientCapabilities_AdvertisesFullReverseFilesystemBridge));
        var provider = CreateProvider(fixture);

        Assert.IsTrue(provider.ClientCapabilities.FileSystemReadText);
        Assert.IsTrue(provider.ClientCapabilities.FileSystemWriteText);
        Assert.IsTrue(provider.ClientCapabilities.Terminal);
        Assert.IsTrue(provider.ClientCapabilities.SessionBooleanConfig);
        Assert.IsTrue(provider.ClientCapabilities.ElicitationFormUrl);
        Assert.IsTrue(provider.ClientCapabilities.TerminalOutputMeta);
    }

    [TestMethod]
    public void Registry_QoderRegistered_DefaultStaysClaudeAndQoderIsDiscoverable()
    {
        using var fixture = new FakeNpmFixture(nameof(Registry_QoderRegistered_DefaultStaysClaudeAndQoderIsDiscoverable));
        var claude = new TestProvider("acp-claude", "Claude Code", new CountingRuntime(fixture.InstallDirectory), []);
        var qoder = CreateProvider(fixture);

        var registry = new AgentProviderRegistry(
            [claude, qoder],
            new AgentProviderOptions { DefaultProviderKey = "acp-claude" });

        Assert.AreSame(claude, registry.DefaultProvider);
        Assert.AreSame(qoder, registry.Find("acp-qoder"));
        CollectionAssert.AreEqual(new IAcpAgentProvider[] { claude, qoder }, registry.Providers.ToArray());
    }

    private static QoderCliAcpAgentProvider CreateProvider(FakeNpmFixture fixture)
        => new(fixture.CreateQoderRuntime());
}
