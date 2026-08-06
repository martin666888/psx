using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class OpencodeAcpRuntimeTests
{
    private const string OpencodeVersion = "1.18.14";

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
        Assert.AreEqual(OpencodeVersion, snapshot.CurrentVersion);
        Assert.IsNull(snapshot.PendingVersion);
        Assert.IsFalse(snapshot.HasPendingUpdate);
        Assert.AreEqual("OpenCode", snapshot.ProductName);
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
        File.Delete(Path.Combine(workspace.Path, "tools", "opencode", "package-lock.json"));
        var runtime = CreateRuntime(workspace.Path, installBundle: false);

        Assert.IsFalse(runtime.IsReady());
        var result = await runtime.EnsureInstalledAsync();
        Assert.AreEqual(AcpRuntimeOperationKind.Failed, result.Kind);
    }

    [TestMethod]
    public void IsReady_MissingResolvedExe_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_MissingResolvedExe_ReturnsFalse));
        InstallBundle(workspace.Path);
        File.Delete(ExePath(workspace.Path));
        var runtime = CreateRuntime(workspace.Path, installBundle: false);

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_NoAvx2_BundledModernInstall_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_NoAvx2_BundledModernInstall_ReturnsFalse));
        var runtime = CreateRuntime(workspace.Path, supportsAvx2: false);

        Assert.IsFalse(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_NoAvx2_BaselineCurrentInstall_ReturnsTrue()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_NoAvx2_BaselineCurrentInstall_ReturnsTrue));
        InstallBundle(workspace.Path);
        var paths = new RuntimeLocator(workspace.Path).Locate();
        WritePackage(
            Path.Combine(paths.OpencodeCurrentDirectory, "node_modules", OpencodeAcpRuntime.BaselinePackageName),
            OpencodeAcpRuntime.BaselinePackageName,
            OpencodeVersion);
        var runtime = CreateRuntime(workspace.Path, installBundle: false, supportsAvx2: false);

        Assert.IsTrue(runtime.IsReady());
    }

    [TestMethod]
    public void IsReady_NoAvx2_ModernCurrentInstall_ReturnsFalse()
    {
        using var workspace = TestWorkspace.Create(nameof(IsReady_NoAvx2_ModernCurrentInstall_ReturnsFalse));
        InstallBundle(workspace.Path);
        var paths = new RuntimeLocator(workspace.Path).Locate();
        WritePackage(
            Path.Combine(paths.OpencodeCurrentDirectory, "node_modules", OpencodeAcpRuntime.ModernPackageName),
            OpencodeAcpRuntime.ModernPackageName,
            OpencodeVersion);
        var runtime = CreateRuntime(workspace.Path, installBundle: false, supportsAvx2: false);

        Assert.IsFalse(runtime.IsReady(), "a modern build in opencode-current cannot execute without AVX2");
        Assert.ThrowsExactly<InvalidOperationException>(() => runtime.CreateProcessSpec(workspace.Path));
    }

    [TestMethod]
    public async Task PrepareForStartup_NoAvx2_ModernNext_RevertsPointerAndKeepsCurrent()
    {
        using var workspace = TestWorkspace.Create(nameof(PrepareForStartup_NoAvx2_ModernNext_RevertsPointerAndKeepsCurrent));
        InstallBundle(workspace.Path);
        var paths = new RuntimeLocator(workspace.Path).Locate();
        WritePackage(
            Path.Combine(paths.OpencodeCurrentDirectory, "node_modules", OpencodeAcpRuntime.BaselinePackageName),
            OpencodeAcpRuntime.BaselinePackageName,
            OpencodeVersion);
        // A modern build staged on different hardware must never be promoted
        // on an AVX2-less machine.
        WritePackage(
            Path.Combine(paths.OpencodeNextDirectory, "node_modules", OpencodeAcpRuntime.ModernPackageName),
            OpencodeAcpRuntime.ModernPackageName,
            "1.19.0");
        Directory.CreateDirectory(paths.RuntimeRoot);
        File.WriteAllText(paths.OpencodeActivePointerFile, "next");
        var runtime = CreateRuntime(workspace.Path, installBundle: false, supportsAvx2: false);

        await runtime.PrepareForStartupAsync();

        Assert.AreEqual("current", File.ReadAllText(paths.OpencodeActivePointerFile).Trim());
        Assert.IsTrue(
            Directory.Exists(Path.Combine(paths.OpencodeCurrentDirectory, "node_modules", OpencodeAcpRuntime.BaselinePackageName)),
            "the baseline current install must survive the aborted promote");
        Assert.IsTrue(runtime.IsReady());
        Assert.AreEqual(OpencodeVersion, runtime.GetVersionSnapshot().CurrentVersion);
    }

    [TestMethod]
    public void CreateProcessSpec_ValidInstall_LaunchesExeWithAcpArgumentAndAutoUpdateDisabled()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_ValidInstall_LaunchesExeWithAcpArgumentAndAutoUpdateDisabled));
        var runtime = CreateRuntime(workspace.Path);
        var cwd = Path.Combine(workspace.Path, "project");
        Directory.CreateDirectory(cwd);

        var spec = runtime.CreateProcessSpec(cwd);

        Assert.AreEqual(ExePath(workspace.Path), spec.FileName);
        Assert.AreEqual(cwd, spec.WorkingDirectory);
        Assert.IsNotNull(spec.Arguments);
        Assert.HasCount(1, spec.Arguments!);
        Assert.AreEqual("acp", spec.Arguments![0]);
        Assert.AreEqual("true", spec.Environment!["OPENCODE_DISABLE_AUTOUPDATE"]);
    }

    [TestMethod]
    public void CreateProcessSpec_CorruptInstall_ThrowsInvalidOperation()
    {
        using var workspace = TestWorkspace.Create(nameof(CreateProcessSpec_CorruptInstall_ThrowsInvalidOperation));
        InstallBundle(workspace.Path);
        File.Delete(ExePath(workspace.Path));
        var runtime = CreateRuntime(workspace.Path, installBundle: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => runtime.CreateProcessSpec(workspace.Path));
    }

    [TestMethod]
    public void TryBuildInteractivePowerShellInvocation_ValidInstall_ReturnsExeWithoutAcp()
    {
        using var workspace = TestWorkspace.Create(nameof(TryBuildInteractivePowerShellInvocation_ValidInstall_ReturnsExeWithoutAcp));
        var runtime = CreateRuntime(workspace.Path);

        var invocation = runtime.TryBuildInteractivePowerShellInvocation(null);

        Assert.IsNotNull(invocation);
        StringAssert.Contains(invocation, ExePath(workspace.Path));
        Assert.IsFalse(invocation!.Contains("acp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuildInteractivePowerShellInvocation_WithExtraArgument_AppendsVerbatim()
    {
        using var workspace = TestWorkspace.Create(nameof(TryBuildInteractivePowerShellInvocation_WithExtraArgument_AppendsVerbatim));
        var runtime = CreateRuntime(workspace.Path);

        var invocation = runtime.TryBuildInteractivePowerShellInvocation("auth login");

        Assert.IsNotNull(invocation);
        StringAssert.Contains(invocation, ExePath(workspace.Path));
        StringAssert.Contains(invocation, "auth login");
    }

    [TestMethod]
    public void BuildStatusText_ReportsBundledVersion()
    {
        using var workspace = TestWorkspace.Create(nameof(BuildStatusText_ReportsBundledVersion));
        var runtime = CreateRuntime(workspace.Path);

        var text = runtime.BuildStatusText();

        StringAssert.Contains(text, OpencodeVersion);
        StringAssert.Contains(text, "OpenCode");
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
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Path, "runtime", "opencode-next")));
    }

    private static OpencodeAcpRuntime CreateRuntime(
        string installDirectory,
        bool installBundle = true,
        bool supportsAvx2 = true)
    {
        if (installBundle)
            InstallBundle(installDirectory);
        return new OpencodeAcpRuntime(
            new RuntimeLocator(installDirectory),
            Path.Combine(installDirectory, "logs"),
            TimeSpan.FromSeconds(10),
            () => supportsAvx2);
    }

    private static string PackageDirectory(string installDirectory) => Path.Combine(
        installDirectory, "tools", "opencode", "node_modules", OpencodeAcpRuntime.ModernPackageName);

    private static string ExePath(string installDirectory) =>
        Path.Combine(PackageDirectory(installDirectory), "bin", "opencode.exe");

    private static void InstallBundle(string installDirectory)
    {
        WriteFile(Path.Combine(installDirectory, "tools", "opencode", "package-lock.json"), "{}");
        WritePackage(PackageDirectory(installDirectory), OpencodeAcpRuntime.ModernPackageName, OpencodeVersion);
    }

    private static void WritePackage(string packageDirectory, string packageName, string version)
    {
        WriteFile(
            Path.Combine(packageDirectory, "package.json"),
            JsonSerializer.Serialize(new { name = packageName, version }));
        WriteFile(Path.Combine(packageDirectory, "bin", "opencode.exe"), "fake opencode executable");
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
