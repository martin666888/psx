using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class KimiCodeAcpRuntimeTests
{
    private const string KimiVersion = "0.29.1";

    [TestMethod]
    public void IsReady_CompleteBundledInstall_ReturnsTrue()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_CompleteBundledInstall_ReturnsTrue));
        var runtime = CreateRuntime(workspace.Path);

        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public void VersionSnapshot_BundledInstall_ReportsVersionWithoutSelfUpdate()
    {
        using var workspace = TestWorkspace.Create(nameof(VersionSnapshot_BundledInstall_ReportsVersionWithoutSelfUpdate));
        var runtime = CreateRuntime(workspace.Path);

        Assert.IsFalse(runtime.SupportsSelfUpdate);
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(KimiVersion, snapshot.CurrentVersion);
        Assert.IsNull(snapshot.PendingVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
    }

    [TestMethod]
    public async Task EnsureInstalled_CompleteBundledInstall_ReportsAlreadyReady()
    {
        using var workspace = TestWorkspace.Create(nameof(EnsureInstalled_CompleteBundledInstall_ReportsAlreadyReady));
        var runtime = CreateRuntime(workspace.Path);

        var result = await runtime.EnsureInstalledAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
    }

    [TestMethod]
    public async Task EnsureInstalled_MissingLockfile_ReturnsFailedNotFakeSuccess()
    {
        using var workspace = TestWorkspace.Create(nameof(EnsureInstalled_MissingLockfile_ReturnsFailedNotFakeSuccess));
        InstallBundle(workspace.Path);
        File.Delete(Path.Combine(workspace.Path, "tools", "kimi", "package-lock.json"));
        var runtime = new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
        var result = await runtime.EnsureInstalledAsync();
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
    }

    [TestMethod]
    public void IsReady_MissingPortableNode_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_MissingPortableNode_ReturnsFalse));
        InstallBundle(workspace.Path);
        File.Delete(Path.Combine(workspace.Path, "tools", "node", "node.exe"));
        var runtime = new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_MissingResolvedEntryFile_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_MissingResolvedEntryFile_ReturnsFalse));
        InstallBundle(workspace.Path);
        File.Delete(EntryPath(workspace.Path));
        var runtime = new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_BinPathEscapesPackageDirectory_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_BinPathEscapesPackageDirectory_ReturnsFalse));
        InstallBundle(workspace.Path, binKimi: "../../../../evil.js");
        var runtime = new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void CreateProcessSpec_ValidInstall_LaunchesPortableNodeWithEntryAndAcp()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_ValidInstall_LaunchesPortableNodeWithEntryAndAcp));
        var runtime = CreateRuntime(workspace.Path);
        var cwd = Path.Combine(workspace.Path, "project");
        Directory.CreateDirectory(cwd);

        var spec = runtime.CreateProcessSpec(cwd);

        Assert.AreEqual(Path.Combine(workspace.Path, "tools", "node", "node.exe"), spec.FileName);
        Assert.AreEqual(cwd, spec.WorkingDirectory);
        Assert.IsNotNull(spec.Arguments);
        Assert.HasCount(2, spec.Arguments!);
        Assert.AreEqual(EntryPath(workspace.Path), spec.Arguments![0]);
        Assert.AreEqual("acp", spec.Arguments![1]);
    }

    [TestMethod]
    public void CreateProcessSpec_CorruptInstall_ThrowsInvalidOperation()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_CorruptInstall_ThrowsInvalidOperation));
        InstallBundle(workspace.Path);
        File.Delete(EntryPath(workspace.Path));
        var runtime = new KimiCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.ThrowsExactly<InvalidOperationException>(() => runtime.CreateProcessSpec(workspace.Path));
    }

    [TestMethod]
    public void BuildStatusText_ReportsBundledVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(BuildStatusText_ReportsBundledVersion));
        var runtime = CreateRuntime(workspace.Path);

        var text = runtime.BuildStatusText();

        StringAssert.Contains(text, KimiVersion);
        StringAssert.Contains(text, "Kimi Code");
    }

    [TestMethod]
    public async Task RefreshAsync_IsNoOp_ReturnsAlreadyReady()
    {
        using var workspace = TestWorkspace.Create(nameof(RefreshAsync_IsNoOp_ReturnsAlreadyReady));
        var runtime = CreateRuntime(workspace.Path);

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.AlreadyReady, result.Kind);
    }

    // ---- helpers ----

    private static KimiCodeAcpRuntime CreateRuntime(string installDirectory)
    {
        InstallBundle(installDirectory);
        return new KimiCodeAcpRuntime(new RuntimeLocator(installDirectory), LogDirectory(installDirectory));
    }

    private static string LogDirectory(string installDirectory) =>
        Path.Combine(installDirectory, "logs");

    private static string KimiPackageDirectory(string installDirectory) => Path.Combine(
        installDirectory, "tools", "kimi", "node_modules", "@moonshot-ai", "kimi-code");

    private static string EntryPath(string installDirectory) =>
        Path.Combine(KimiPackageDirectory(installDirectory), "dist", "main.mjs");

    private static void InstallBundle(string installDirectory, string binKimi = "dist/main.mjs")
    {
        // Portable Node.
        WriteFile(Path.Combine(installDirectory, "tools", "node", "node.exe"), "fake node");

        // Pinned lockfile.
        WriteFile(Path.Combine(installDirectory, "tools", "kimi", "package-lock.json"), "{}");

        // Kimi package manifest with a bin.kimi entry.
        var packageDir = KimiPackageDirectory(installDirectory);
        WriteFile(
            Path.Combine(packageDir, "package.json"),
            JsonSerializer.Serialize(new
            {
                name = "@moonshot-ai/kimi-code",
                version = KimiVersion,
                bin = new { kimi = binKimi }
            }));

        // Expected entry file (dist/main.mjs).
        WriteFile(EntryPath(installDirectory), "// fake kimi acp entry");
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
