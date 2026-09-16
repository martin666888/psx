using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class PiProviderTests
{
    [TestMethod]
    public async Task Runtime_BundledPiIsReadyWithoutNpmOrInstallation()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        Write(workspace.Path, "tools/node/node.exe", "fake");
        Write(workspace.Path, "tools/pi-launcher/launch.mjs", "fake");
        using var runtime = new PiAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        Install(runtime.BundledDirectory);
        await runtime.PrepareForStartupAsync();
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, (await runtime.EnsureInstalledAsync()).Kind);
        Assert.IsFalse(Directory.Exists(runtime.CurrentDirectory));
        Assert.AreEqual("0.85.1", runtime.GetVersionSnapshot().CurrentVersion);
        StringAssert.StartsWith(runtime.CreateProcessSpec(workspace.Path).Arguments[1], runtime.BundledDirectory);
        StringAssert.Contains(runtime.InteractiveCommand(null)!, runtime.BundledDirectory);
    }

    [TestMethod]
    public async Task Runtime_UpdateOverridesBundle_AndCorruptCurrentFallsBackToBundle()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        Write(workspace.Path, "tools/node/node.exe", "fake");
        Write(workspace.Path, "tools/pi-launcher/launch.mjs", "fake");
        using var runtime = new PiAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        Install(runtime.BundledDirectory);
        Install(runtime.NextDirectory, "0.86.0");
        File.WriteAllText(runtime.PointerFile, "next");
        Assert.AreEqual("0.85.1", runtime.GetVersionSnapshot().CurrentVersion);
        await runtime.PrepareForStartupAsync();
        Assert.AreEqual("0.86.0", runtime.GetVersionSnapshot().CurrentVersion);
        StringAssert.StartsWith(runtime.CreateProcessSpec(workspace.Path).Arguments[1], runtime.CurrentDirectory);
        File.Delete(Path.Combine(runtime.CurrentDirectory, "package-lock.json"));
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual("0.85.1", runtime.GetVersionSnapshot().CurrentVersion);
    }

    [TestMethod]
    public void Provider_UsesIndependentIdentityAndStandardSessionParameters()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        using var runtime = new PiAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        var provider = new PiAcpAgentProvider(runtime);
        Assert.AreEqual("acp-pi", provider.Descriptor.Key);
        Assert.AreEqual("pi", provider.Descriptor.IconKey);
        Assert.IsNotNull(provider.UsageSource);
        Assert.IsNotNull(provider.ConfigSource);
        Assert.IsFalse(provider.ClientCapabilities.FileSystemWriteText);
        Assert.IsFalse(provider.ClientCapabilities.Terminal);
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(provider.CreateRestoreSessionParameters("session", workspace.Path)));
        Assert.AreEqual("session", parameters.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual(0, parameters.RootElement.GetProperty("mcpServers").GetArrayLength());
        Assert.IsNull(provider.CreateLoginTerminalProfile(workspace.Path));
        Assert.IsTrue(provider.IsCommandVisible("/skill:review"));
    }

    [TestMethod]
    public async Task Runtime_StartupPromotesLocally_AndLaunchesManagedEntries()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        Write(workspace.Path, "tools/node/node.exe", "fake");
        Write(workspace.Path, "tools/pi-launcher/launch.mjs", "fake");
        using var runtime = new PiAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        Install(runtime.NextDirectory);
        File.WriteAllText(runtime.PointerFile, "next");
        Assert.IsTrue(runtime.GetVersionSnapshot().HasPendingUpdate);
        Assert.IsFalse(runtime.IsReady());
        await runtime.PrepareForStartupAsync();
        Assert.IsTrue(runtime.IsReady());
        Assert.IsFalse(runtime.GetVersionSnapshot().HasPendingUpdate);
        var spec = runtime.CreateProcessSpec(workspace.Path);
        Assert.AreEqual(Path.Combine(workspace.Path, "tools", "node", "node.exe"), spec.FileName);
        Assert.HasCount(3, spec.Arguments);
        Assert.IsTrue(spec.Arguments.All(Path.IsPathFullyQualified));
        Assert.AreEqual("0.85.1", runtime.GetVersionSnapshot().CurrentVersion);
        var profile = new PiAcpAgentProvider(runtime).CreateNativeTerminalProfile(workspace.Path, "id'with quote");
        StringAssert.Contains(profile!.Arguments, "--session 'id''with quote'");
    }

    [TestMethod]
    public async Task Runtime_InvalidPendingTreeDoesNotReplaceCurrent()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        using var runtime = new PiAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        Install(runtime.CurrentDirectory);
        Directory.CreateDirectory(runtime.NextDirectory);
        File.WriteAllText(runtime.PointerFile, "next");
        await runtime.PrepareForStartupAsync();
        Assert.AreEqual("current", File.ReadAllText(runtime.PointerFile));
        Assert.IsTrue(PiAcpRuntime.ValidateRoot(runtime.CurrentDirectory));
        var result = await runtime.EnsureInstalledAsync();
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
    }

    [TestMethod]
    public async Task Runtime_StartupRetriesTransientFileLocks()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        PiProviderTests.Write(workspace.Path, "tools/node/node.exe", "fake");
        PiProviderTests.Write(workspace.Path, "tools/pi-launcher/launch.mjs", "fake");
        using var runtime = new PiAcpRuntime(new RuntimeLocator(workspace.Path), Path.Combine(workspace.Path, "logs"));
        Install(runtime.NextDirectory);
        File.WriteAllText(runtime.PointerFile, "next");
        var locked = new FileStream(Path.Combine(runtime.NextDirectory, "package-lock.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            locked.Dispose();
        });
        try
        {
            await runtime.PrepareForStartupAsync();
            Assert.IsTrue(runtime.IsReady());
        }
        finally
        {
            await release;
            locked.Dispose();
        }
    }

    [TestMethod]
    [DataRow("../escape.js")]
    [DataRow("../../escape.js")]
    public void Runtime_RejectsPackageEntryEscape(string entry)
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        Install(workspace.Path);
        Write(workspace.Path, "node_modules/pi-acp/package.json", JsonSerializer.Serialize(new
        { name = "pi-acp", version = "0.0.33", bin = new Dictionary<string, string> { ["pi-acp"] = entry } }));
        Write(workspace.Path, "node_modules/escape.js", "bad");
        Write(workspace.Path, "escape.js", "bad");
        Assert.IsNull(PiAcpRuntime.Entry(workspace.Path, "pi-acp", "pi-acp"));
        Assert.IsFalse(PiAcpRuntime.ValidateRoot(workspace.Path));
    }

    [TestMethod]
    public void Config_ProjectsModelsAndCredentialNamesWithoutValues()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        Write(workspace.Path, "settings.json", """{"defaultModel":"model-a"}""");
        Write(workspace.Path, "models.json", """{"providers":{"custom":{"apiKey":"secret-key","headers":{"Authorization":"secret-token"},"baseUrl":"https://name:password@example.com/v1?key=secret","models":[{"id":"model-a","name":"A"}]}}}""");
        Write(workspace.Path, "auth.json", """{"custom":{"access":"secret-access","refresh":"secret-refresh"}}""");
        Write(workspace.Path, "skills/review/SKILL.md", "private instructions");
        var report = new PiConfigSource(() => workspace.Path).Collect(default);
        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.AreEqual("https://example.com/v1", report.Models.Single().BaseUrl);
        Assert.AreEqual("review", report.Skills.Single().Name);
        var wire = JsonSerializer.Serialize(report);
        Assert.IsFalse(wire.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains("password", StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains("private instructions", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Config_MalformedFileReportsPartial()
    {
        using var workspace = TestWorkspace.Create(nameof(PiProviderTests));
        Write(workspace.Path, "models.json", "bad-json");
        var report = new PiConfigSource(() => workspace.Path).Collect(default);
        Assert.AreEqual(AgentProviderConfigReport.Partial, report.State);
        CollectionAssert.Contains(report.Notes.ToArray(), AgentConfigNotes.ParseFailed);
    }

    internal static void Install(string root, string piVersion = "0.85.1", string adapterVersion = "0.0.33")
    {
        Write(root, "package-lock.json", "{}");
        foreach (var (package, bin, version) in new[] { (PiAcpRuntime.PiPackage, "pi", piVersion), ("pi-acp", "pi-acp", adapterVersion) })
        {
            Write(root, "node_modules/" + package + "/package.json", JsonSerializer.Serialize(new
            { name = package, version, bin = new Dictionary<string, string> { [bin] = "dist/index.js" } }));
            Write(root, "node_modules/" + package + "/dist/index.js", "fake");
        }
    }

    internal static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
