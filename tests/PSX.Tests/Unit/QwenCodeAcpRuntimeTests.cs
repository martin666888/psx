using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QwenCodeAcpRuntimeTests
{
    private const string QwenVersion = "0.21.5";

    [TestMethod]
    public void IsReady_CompleteBundledInstall_ReturnsTrue()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_CompleteBundledInstall_ReturnsTrue));
        var runtime = CreateRuntime(workspace.Path);

        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public void VersionSnapshot_BundledInstall_ReportsBundledVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(VersionSnapshot_BundledInstall_ReportsBundledVersion));
        var runtime = CreateRuntime(workspace.Path);

        Assert.IsTrue(runtime.SupportsSelfUpdate);
        var snapshot = runtime.GetVersionSnapshot();
        Assert.AreEqual(QwenVersion, snapshot.CurrentVersion);
        Assert.IsNull(snapshot.PendingVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        Assert.AreEqual("Qwen Code", snapshot.ProductName);
        Assert.IsNull(snapshot.TechnicalDetails);
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
        File.Delete(Path.Combine(workspace.Path, "tools", "qwen", "package-lock.json"));
        var runtime = new QwenCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

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
        var runtime = new QwenCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_MissingResolvedEntryFile_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_MissingResolvedEntryFile_ReturnsFalse));
        InstallBundle(workspace.Path);
        File.Delete(EntryPath(workspace.Path));
        var runtime = new QwenCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_BinPathEscapesPackageDirectory_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_BinPathEscapesPackageDirectory_ReturnsFalse));
        InstallBundle(workspace.Path, binQwen: "../../../../evil.js");
        var runtime = new QwenCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void CreateProcessSpec_ValidInstall_LaunchesPortableNodeWithEntryAndAcpFlag()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_ValidInstall_LaunchesPortableNodeWithEntryAndAcpFlag));
        var runtime = CreateRuntime(workspace.Path);
        var cwd = Path.Combine(workspace.Path, "project");
        Directory.CreateDirectory(cwd);

        var spec = runtime.CreateProcessSpec(cwd);

        Assert.AreEqual(Path.Combine(workspace.Path, "tools", "node", "node.exe"), spec.FileName);
        Assert.AreEqual(cwd, spec.WorkingDirectory);
        Assert.IsNotNull(spec.Arguments);
        Assert.HasCount(2, spec.Arguments!);
        Assert.AreEqual(EntryPath(workspace.Path), spec.Arguments![0]);
        Assert.AreEqual("--acp", spec.Arguments![1]);
    }

    [TestMethod]
    public void CreateProcessSpec_ValidInstall_WritesSystemSettingsWithAutoUpdateDisabled()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_ValidInstall_WritesSystemSettingsWithAutoUpdateDisabled));
        var runtime = CreateRuntime(workspace.Path);
        var paths = new RuntimeLocator(workspace.Path).Locate();
        var settingsPath = Path.Combine(paths.RuntimeRoot, "qwen-system-settings.json");

        var spec = runtime.CreateProcessSpec(workspace.Path);

        Assert.IsTrue(File.Exists(settingsPath));
        Assert.AreEqual(settingsPath, spec.Environment!["QWEN_CODE_SYSTEM_SETTINGS_PATH"]);
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.IsFalse(document.RootElement.GetProperty("general").GetProperty("enableAutoUpdate").GetBoolean());
    }

    [TestMethod]
    public void CreateProcessSpec_ExistingSettingsWithAutoUpdateEnabled_RewritesToDisabled()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_ExistingSettingsWithAutoUpdateEnabled_RewritesToDisabled));
        var runtime = CreateRuntime(workspace.Path);
        var paths = new RuntimeLocator(workspace.Path).Locate();
        Directory.CreateDirectory(paths.RuntimeRoot);
        var settingsPath = Path.Combine(paths.RuntimeRoot, "qwen-system-settings.json");
        File.WriteAllText(settingsPath, "{\"general\":{\"enableAutoUpdate\":true}}");

        runtime.CreateProcessSpec(workspace.Path);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.IsFalse(document.RootElement.GetProperty("general").GetProperty("enableAutoUpdate").GetBoolean());
    }

    [TestMethod]
    public void CreateProcessSpec_CorruptInstall_ThrowsInvalidOperation()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_CorruptInstall_ThrowsInvalidOperation));
        InstallBundle(workspace.Path);
        File.Delete(EntryPath(workspace.Path));
        var runtime = new QwenCodeAcpRuntime(new RuntimeLocator(workspace.Path), LogDirectory(workspace.Path));

        Assert.ThrowsExactly<InvalidOperationException>(() => runtime.CreateProcessSpec(workspace.Path));
    }

    [TestMethod]
    public void TryBuildInteractivePowerShellInvocation_ValidInstall_ReturnsNodeAndEntryWithoutAcp()
    {
        using var workspace = TestWorkspace.Create(nameof(TryBuildInteractivePowerShellInvocation_ValidInstall_ReturnsNodeAndEntryWithoutAcp));
        var runtime = CreateRuntime(workspace.Path);
        var nodePath = Path.Combine(workspace.Path, "tools", "node", "node.exe");
        var entryPath = EntryPath(workspace.Path);

        var invocation = runtime.TryBuildInteractivePowerShellInvocation(null);

        Assert.IsNotNull(invocation);
        StringAssert.Contains(invocation, nodePath);
        StringAssert.Contains(invocation, entryPath);
        Assert.IsFalse(invocation!.Contains("--acp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuildInteractivePowerShellInvocation_WithSessionId_IncludesResumeFlag()
    {
        using var workspace = TestWorkspace.Create(nameof(TryBuildInteractivePowerShellInvocation_WithSessionId_IncludesResumeFlag));
        var runtime = CreateRuntime(workspace.Path);

        var invocation = runtime.TryBuildInteractivePowerShellInvocation("session-abc");

        Assert.IsNotNull(invocation);
        StringAssert.Contains(invocation, "--resume");
        StringAssert.Contains(invocation, "session-abc");
    }

    [TestMethod]
    public void BuildStatusText_ReportsBundledVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(BuildStatusText_ReportsBundledVersion));
        var runtime = CreateRuntime(workspace.Path);

        var text = runtime.BuildStatusText();

        StringAssert.Contains(text, QwenVersion);
        StringAssert.Contains(text, "Qwen Code");
        StringAssert.Contains(text, "随包内置");
    }

    [TestMethod]
    public async Task RefreshAsync_WithoutPortableNpm_FailsWithoutTouchingInstall()
    {
        using var workspace = TestWorkspace.Create(nameof(RefreshAsync_WithoutPortableNpm_FailsWithoutTouchingInstall));
        var runtime = CreateRuntime(workspace.Path);

        var result = await runtime.RefreshAsync();

        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
        Assert.IsTrue(runtime.IsReady());
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Path, "runtime", "qwen-next")));
    }

    private static QwenCodeAcpRuntime CreateRuntime(string installDirectory)
    {
        InstallBundle(installDirectory);
        return new QwenCodeAcpRuntime(new RuntimeLocator(installDirectory), LogDirectory(installDirectory));
    }

    private static string LogDirectory(string installDirectory) =>
        Path.Combine(installDirectory, "logs");

    private static string QwenPackageDirectory(string installDirectory) => Path.Combine(
        installDirectory, "tools", "qwen", "node_modules", "@qwen-code", "qwen-code");

    private static string EntryPath(string installDirectory) =>
        Path.Combine(QwenPackageDirectory(installDirectory), "cli-entry.js");

    private static void InstallBundle(string installDirectory, string binQwen = "cli-entry.js")
    {
        WriteFile(Path.Combine(installDirectory, "tools", "node", "node.exe"), "fake node");
        WriteFile(Path.Combine(installDirectory, "tools", "qwen", "package-lock.json"), "{}");

        var packageDir = QwenPackageDirectory(installDirectory);
        WriteFile(
            Path.Combine(packageDir, "package.json"),
            JsonSerializer.Serialize(new
            {
                name = "@qwen-code/qwen-code",
                version = QwenVersion,
                bin = new { qwen = binQwen }
            }));

        WriteFile(EntryPath(installDirectory), "// fake qwen acp entry");
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
