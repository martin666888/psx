using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DshRegistryPresetTests
{
    [TestMethod]
    public void Store_SameRegistry_IsNoOp()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_SameRegistry_IsNoOp));
        var store = new PsxEnvironmentSettingsStore(workspace.Path);
        var first = store.SetDshRegistry(DshRegistryDescriptor.NpmmirrorKey);
        Assert.IsTrue(first.Success);
        Assert.IsTrue(first.Changed);
        var revision = first.Snapshot.Revision;

        var again = store.SetDshRegistry(DshRegistryDescriptor.NpmmirrorKey);
        Assert.IsTrue(again.Success);
        Assert.IsFalse(again.Changed);
        Assert.AreEqual(revision, again.Snapshot.Revision);
    }

    [TestMethod]
    public void Store_CorruptDocument_FallsBackToOfficial()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_CorruptDocument_FallsBackToOfficial));
        File.WriteAllText(Path.Combine(workspace.Path, "environment.json"), "{not-json");
        var store = new PsxEnvironmentSettingsStore(workspace.Path);
        Assert.AreEqual(DshRegistryDescriptor.OfficialKey, store.GetSnapshot().DshRegistry);
    }

    [TestMethod]
    public async Task Coordinator_GetReplyCarriesRequestId_BroadcastDoesNot()
    {
        using var workspace = TestWorkspace.Create(nameof(Coordinator_GetReplyCarriesRequestId_BroadcastDoesNot));
        var bridge = new RecordingAgentBridgeService();
        var coordinator = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(workspace.Path), bridge);

        await coordinator.HandleCommandAsync("req-get", "get", null);
        var get = bridge.Events.Single(e => e.GetProperty("type").GetString() == "app_settings_snapshot");
        Assert.AreEqual("req-get", get.GetProperty("requestId").GetString());

        await coordinator.HandleCommandAsync("req-set", "set_dsh_registry", DshRegistryDescriptor.NpmmirrorKey);
        var snapshots = bridge.Events
            .Where(e => e.GetProperty("type").GetString() == "app_settings_snapshot")
            .Skip(1)
            .ToArray();
        Assert.HasCount(2, snapshots);
        Assert.AreEqual("req-set", snapshots[0].GetProperty("requestId").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshots[1].GetProperty("requestId").ValueKind);
        Assert.AreEqual("npmmirror", snapshots[1].GetProperty("dshRegistry").GetString());
    }

    [TestMethod]
    public async Task Coordinator_SameRegistrySet_DoesNotBroadcast()
    {
        using var workspace = TestWorkspace.Create(nameof(Coordinator_SameRegistrySet_DoesNotBroadcast));
        var bridge = new RecordingAgentBridgeService();
        var coordinator = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(workspace.Path), bridge);
        await coordinator.HandleCommandAsync("a", "set_dsh_registry", DshRegistryDescriptor.OfficialKey);
        Assert.HasCount(1, bridge.Events);
        Assert.AreEqual("a", bridge.Events[0].GetProperty("requestId").GetString());
    }

    [TestMethod]
    public void Sri_RejectsShortPrefixAndWrongLength()
    {
        Assert.IsFalse(DshSri.IsValid("sha512-test"));
        Assert.IsFalse(DshSri.IsValid("sha256-" + Convert.ToBase64String(new byte[64])));
        Assert.IsTrue(DshSri.IsValid(DshSri.TestIntegrity));
        Assert.IsFalse(DshSri.IsValid(DshSri.TestIntegrity.Replace("sha512-", "sha512-\t", StringComparison.Ordinal)));
        Assert.IsFalse(DshSri.IsValid(DshSri.TestIntegrity + "\n"));
        Assert.IsFalse(DshSri.IsValid(" " + DshSri.TestIntegrity));
        Assert.IsFalse(DshSri.IsTrustedRegistryUrl(
            "https://registry.npmjs.org.evil.example/pkg.tgz",
            DshRegistryDescriptor.OfficialOrigin));
        Assert.IsTrue(DshSri.IsTrustedRegistryUrl(
            "https://registry.npmmirror.com/@deepseek-ai/dsh/-/dsh-1.tgz",
            DshRegistryDescriptor.NpmmirrorOrigin));
    }

    [TestMethod]
    [DataRow("official")]
    [DataRow("npmmirror")]
    public async Task StageUpdate_CatalogSriMismatch_RejectedBeforeCi(string registryKey)
    {
        var registry = DshRegistryDescriptor.TryGet(registryKey, out var descriptor)
            ? descriptor
            : DshRegistryDescriptor.Official;
        using var workspace = TestWorkspace.Create(
            nameof(StageUpdate_CatalogSriMismatch_RejectedBeforeCi) + "_" + registryKey);
        var marker = Path.Combine(workspace.Path, "lifecycle.txt");
        var runs = Path.Combine(workspace.Path, "npm-runs.txt");
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        // The lock bytes pin TestIntegrity while the catalog claims OtherSri:
        // the shared validator must reject this before any npm invocation.
        locks.Bundle(
            "0.1.1-rc.3",
            sri: OtherSri(),
            lockJson: FakeDshLockSource.BuildLockJson("0.1.1-rc.3", DshSri.TestIntegrity));
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths, marker: marker, runs: runs);

        var result = await runtime.StageUpdateAsync(registry, "0.1.1-rc.3", CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.IntegrityFailed, result.ErrorClass);
        Assert.IsFalse(File.Exists(marker), "npm ci must not run when the bundled SRI mismatches");
        Assert.IsFalse(File.Exists(runs));
        Assert.IsFalse(File.Exists(paths.DshActivePointerFile));
        Assert.IsFalse(runtime.ApplyStagedUpdate("0.1.1-rc.3"));
    }

    [TestMethod]
    public async Task StageUpdate_CancelledAtValidationStart_KeepsCurrentAndPointer()
    {
        using var workspace = TestWorkspace.Create(
            nameof(StageUpdate_CancelledAtValidationStart_KeepsCurrentAndPointer));
        var marker = Path.Combine(workspace.Path, "lifecycle.txt");
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths, marker: marker);

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.StageUpdateAsync(
                DshRegistryDescriptor.Official,
                "0.1.1-rc.3",
                cts.Token,
                () => cts.Cancel()));

        Assert.IsTrue(File.Exists(marker), "validationStarted now fires after npm ci completed");
        Assert.IsFalse(File.Exists(paths.DshActivePointerFile));
        Assert.IsFalse(runtime.ApplyStagedUpdate("0.1.1-rc.3"));
        Assert.AreEqual(DshWebRuntime.SeededPackageVersion, runtime.CurrentVersion);
    }

    [TestMethod]
    public async Task StageUpdate_LockHashChangedDuringCi_RejectsWithoutPointer()
    {
        using var workspace = TestWorkspace.Create(
            nameof(StageUpdate_LockHashChangedDuringCi_RejectsWithoutPointer));
        var marker = Path.Combine(workspace.Path, "lifecycle.txt");
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths, marker: marker, mutateLockOnCi: true);

        var result = await runtime.StageUpdateAsync(
            DshRegistryDescriptor.Official,
            "0.1.1-rc.3",
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.IntegrityFailed, result.ErrorClass);
        Assert.IsTrue(File.Exists(marker), "npm ci may run scripts after the lock was validated");
        Assert.IsFalse(File.Exists(paths.DshActivePointerFile));
        Assert.IsFalse(runtime.ApplyStagedUpdate("0.1.1-rc.3"));
    }

    [TestMethod]
    public async Task StageUpdate_NoLockForVersion_ReturnsLockUnavailable_WithoutStagingWrites()
    {
        using var workspace = TestWorkspace.Create(
            nameof(StageUpdate_NoLockForVersion_ReturnsLockUnavailable_WithoutStagingWrites));
        var runs = Path.Combine(workspace.Path, "npm-runs.txt");
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths, runs: runs);

        var result = await runtime.StageUpdateAsync(
            DshRegistryDescriptor.Official,
            "0.1.1-rc.4",
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.LockUnavailable, result.ErrorClass);
        Assert.AreEqual(1, locks.FindCalls, "exactly one lookup happens (the catalog is not re-read)");
        Assert.IsFalse(File.Exists(runs), "no npm invocation is allowed before a lock artifact is found");
        Assert.IsFalse(Directory.Exists(paths.DshNextDirectory));
        Assert.IsFalse(File.Exists(paths.DshActivePointerFile));
    }

    [TestMethod]
    public async Task StageUpdate_CorruptCatalog_ReturnsCatalogCorrupt_WithoutStagingWrites()
    {
        using var workspace = TestWorkspace.Create(
            nameof(StageUpdate_CorruptCatalog_ReturnsCatalogCorrupt_WithoutStagingWrites));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource { CatalogValid = false };
        locks.Bundle("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths);

        var result = await runtime.StageUpdateAsync(
            DshRegistryDescriptor.Official,
            "0.1.1-rc.3",
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.CatalogCorrupt, result.ErrorClass);
        Assert.AreEqual("PSX 安装文件损坏，请重新安装。", result.ErrorMessage);
        Assert.IsFalse(Directory.Exists(paths.DshNextDirectory));
    }

    [TestMethod]
    public async Task StageUpdate_CorruptArtifact_ReturnsCatalogCorrupt_WithoutStagingWrites()
    {
        using var workspace = TestWorkspace.Create(
            nameof(StageUpdate_CorruptArtifact_ReturnsCatalogCorrupt_WithoutStagingWrites));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.3");
        locks.Corrupt("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths);

        var result = await runtime.StageUpdateAsync(
            DshRegistryDescriptor.Official,
            "0.1.1-rc.3",
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.CatalogCorrupt, result.ErrorClass);
        Assert.AreEqual("PSX 安装文件损坏，请重新安装。", result.ErrorMessage);
        Assert.IsFalse(Directory.Exists(paths.DshNextDirectory));
    }

    [TestMethod]
    public async Task InstallAsync_DoesNotQueryLockSource()
    {
        using var workspace = TestWorkspace.Create(nameof(InstallAsync_DoesNotQueryLockSource));
        var locator = new RuntimeLocator(workspace.Path);
        var runtime = new DshWebRuntime(
            locator,
            Path.Combine(workspace.Path, "logs"),
            TimeSpan.FromMinutes(1),
            new ThrowingLockSource());
        var paths = locator.Locate();
        SeedScriptedNpm(paths, "process.exit(1);");

        var result = await runtime.InstallAsync(DshRegistryDescriptor.Npmmirror, CancellationToken.None);
        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CheckForUpdate_CorruptCatalog_FailsWithReinstallMessage()
    {
        using var workspace = TestWorkspace.Create(nameof(CheckForUpdate_CorruptCatalog_FailsWithReinstallMessage));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource { CatalogValid = false };
        locks.Bundle("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedScriptedNpm(paths, "console.log(JSON.stringify('0.1.1-rc.3'));\n");

        var result = await runtime.CheckForUpdateAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.CatalogCorrupt, result.ErrorClass);
        Assert.AreEqual("PSX 安装文件损坏，请重新安装。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task CheckForUpdate_CorruptArtifact_FailsWithReinstallMessage()
    {
        using var workspace = TestWorkspace.Create(nameof(CheckForUpdate_CorruptArtifact_FailsWithReinstallMessage));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.3");
        locks.Corrupt("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedScriptedNpm(paths, "console.log(JSON.stringify('0.1.1-rc.3'));\n");

        var result = await runtime.CheckForUpdateAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(DshErrorClass.CatalogCorrupt, result.ErrorClass);
    }

    [TestMethod]
    public async Task CheckForUpdate_PartitionsInstallableDeferredAndBlocked()
    {
        using var workspace = TestWorkspace.Create(nameof(CheckForUpdate_PartitionsInstallableDeferredAndBlocked));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.4");
        locks.Block("0.1.1-rc.5");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedScriptedNpm(
            paths,
            """
            console.log(JSON.stringify({
              versions: ['0.1.1-rc.2', '0.1.1-rc.3', '0.1.1-rc.4', '0.1.1-rc.5', '0.1.1-rc.6'],
              'dist-tags': { latest: '0.1.1-rc.6' }
            }));
            """);

        var result = await runtime.CheckForUpdateAsync(CancellationToken.None);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsTrue(result.UpdateAvailable);
        Assert.AreEqual("0.1.1-rc.4", result.AvailableVersion);
        Assert.HasCount(1, result.AvailableVersions);
        CollectionAssert.AreEqual(
            new[] { "0.1.1-rc.6", "0.1.1-rc.3" },
            result.DeferredList.Select(entry => entry.Version).ToArray());
        CollectionAssert.AreEqual(
            new[] { "0.1.1-rc.5" },
            result.BlockedList.Select(entry => entry.Version).ToArray());
    }

    [TestMethod]
    public async Task CheckForUpdate_AllNewerDeferred_ReportsDeferredOnly()
    {
        using var workspace = TestWorkspace.Create(nameof(CheckForUpdate_AllNewerDeferred_ReportsDeferredOnly));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Block("0.1.1-rc.4");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedScriptedNpm(
            paths,
            """
            console.log(JSON.stringify({
              versions: ['0.1.1-rc.3', '0.1.1-rc.4'],
              'dist-tags': { latest: '0.1.1-rc.4' }
            }));
            """);

        var result = await runtime.CheckForUpdateAsync(CancellationToken.None);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsFalse(result.UpdateAvailable, "nothing is installable; up_to_date would be a lie");
        Assert.IsNull(result.AvailableVersion);
        Assert.HasCount(0, result.AvailableVersions);
        CollectionAssert.AreEqual(
            new[] { "0.1.1-rc.3" },
            result.DeferredList.Select(entry => entry.Version).ToArray());
        CollectionAssert.AreEqual(
            new[] { "0.1.1-rc.4" },
            result.BlockedList.Select(entry => entry.Version).ToArray());
    }

    [TestMethod]
    public async Task ReceiptV3_RoundTrip_AndStagedValidationUsesOfficialOriginAndLockSha()
    {
        using var workspace = TestWorkspace.Create(
            nameof(ReceiptV3_RoundTrip_AndStagedValidationUsesOfficialOriginAndLockSha));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.3");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedFakeDshTree(paths.DshCurrentDirectory, DshWebRuntime.SeededPackageVersion);
        SeedCiNpm(paths);

        var result = await runtime.StageUpdateAsync(
            DshRegistryDescriptor.Npmmirror,
            "0.1.1-rc.3",
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsNull(DshWebRuntime.ValidateStagedUpdate(paths.DshNextDirectory, "0.1.1-rc.3"));

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(paths.DshNextDirectory, "psx-dsh-update.json")));
        Assert.IsTrue(DshWebRuntime.TryReadUpdateReceipt(document.RootElement, out var receipt));
        Assert.AreEqual(3, receipt.SchemaVersion);
        Assert.AreEqual("bundled", receipt.LockSource);
        Assert.AreEqual(
            FakeDshLockSource.HashText(FakeDshLockSource.BuildLockJson("0.1.1-rc.3", DshSri.TestIntegrity)),
            receipt.LockSha256,
            ignoreCase: true);
        Assert.AreEqual(DshRegistryDescriptor.NpmmirrorKey, receipt.RegistryKey);
        Assert.IsTrue(DshSri.Equal(receipt.Integrity, DshSri.TestIntegrity));

        // v3 validates against the official origin even for a mirror install.
        var lockPath = Path.Combine(paths.DshNextDirectory, "package-lock.json");
        File.WriteAllText(
            lockPath,
            FakeDshLockSource.BuildLockJson("0.1.1-rc.3", DshSri.TestIntegrity)
                .Replace("registry.npmjs.org", "registry.npmmirror.com", StringComparison.Ordinal));
        Assert.IsNotNull(DshWebRuntime.ValidateStagedUpdate(paths.DshNextDirectory, "0.1.1-rc.3"));

        // Same logical lock, different bytes: the recorded lock hash must
        // pin the exact staged bytes.
        File.WriteAllText(
            lockPath,
            FakeDshLockSource.BuildLockJson("0.1.1-rc.3", DshSri.TestIntegrity) + "\n");
        Assert.IsNotNull(DshWebRuntime.ValidateStagedUpdate(paths.DshNextDirectory, "0.1.1-rc.3"));

        // Official origin but a different SRI still fails the root check.
        File.WriteAllText(
            lockPath,
            FakeDshLockSource.BuildLockJson("0.1.1-rc.3", OtherSri()));
        Assert.IsNotNull(DshWebRuntime.ValidateStagedUpdate(paths.DshNextDirectory, "0.1.1-rc.3"));
    }

    [TestMethod]
    public void V2MirrorTree_StillPassesStartupGate()
    {
        using var workspace = TestWorkspace.Create(nameof(V2MirrorTree_StillPassesStartupGate));
        var locator = new RuntimeLocator(workspace.Path);
        var runtime = new DshWebRuntime(locator, Path.Combine(workspace.Path, "logs"));
        var paths = locator.Locate();
        SeedFakeNodeToolchain(paths);
        SeedMirrorV2UpdateTree(paths.DshCurrentDirectory, "0.1.1-rc.3");
        Assert.IsTrue(runtime.IsInstalled());
        Assert.AreEqual("0.1.1-rc.3", runtime.CurrentVersion);
    }

    [TestMethod]
    public void V1OfficialReceipt_StillAuthorizesLaunch()
    {
        using var workspace = TestWorkspace.Create(nameof(V1OfficialReceipt_StillAuthorizesLaunch));
        var locator = new RuntimeLocator(workspace.Path);
        var runtime = new DshWebRuntime(locator, Path.Combine(workspace.Path, "logs"));
        SeedFakeNodeToolchain(locator.Locate());
        SeedAuthorizedDshUpdateTree(locator.Locate().DshCurrentDirectory, "0.1.1-rc.3");
        Assert.IsTrue(runtime.IsInstalled());
        Assert.AreEqual("0.1.1-rc.3", runtime.CurrentVersion);
    }

    [TestMethod]
    public async Task IdleRegistryChange_ClearsCatalog()
    {
        using var workspace = TestWorkspace.Create(nameof(IdleRegistryChange_ClearsCatalog));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.4");
        locks.Block("0.1.1-rc.5");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedAuthorizedDshUpdateTree(paths.DshCurrentDirectory, "0.1.1-rc.3");
        SeedScriptedNpm(
            paths,
            """
            console.log(JSON.stringify({
              versions: ['0.1.1-rc.4', '0.1.1-rc.5'],
              'dist-tags': { latest: '0.1.1-rc.5' }
            }));
            """);
        var bridge = new RecordingAgentBridgeService();
        var settings = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(Path.Combine(workspace.Path, "env")), bridge);
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"),
            Path.Combine(workspace.Path, "dsh-workspace"), settings);

        await supervisor.CheckForUpdateAsync();
        var available = LastRuntimeStatus(bridge);
        Assert.AreEqual("available", available.GetProperty("updateState").GetString());
        Assert.AreEqual(1, available.GetProperty("availableVersions").GetArrayLength());
        Assert.AreEqual(0, available.GetProperty("deferredVersions").GetArrayLength());
        Assert.AreEqual(1, available.GetProperty("blockedVersions").GetArrayLength());

        Assert.IsTrue(settings.SetDshRegistry(DshRegistryDescriptor.NpmmirrorKey).Changed);
        var idle = LastRuntimeStatus(bridge);
        Assert.AreEqual("idle", idle.GetProperty("updateState").GetString());
        Assert.AreEqual(0, idle.GetProperty("availableVersions").GetArrayLength());
        Assert.AreEqual(0, idle.GetProperty("deferredVersions").GetArrayLength());
        Assert.AreEqual(0, idle.GetProperty("blockedVersions").GetArrayLength());
    }

    [TestMethod]
    public async Task CheckCompletion_UnrelatedRevisionBump_KeepsCatalog()
    {
        using var workspace = TestWorkspace.Create(nameof(CheckCompletion_UnrelatedRevisionBump_KeepsCatalog));
        var locator = new RuntimeLocator(workspace.Path);
        var locks = new FakeDshLockSource();
        locks.Bundle("0.1.1-rc.4");
        var runtime = CreateRuntime(locator, workspace, locks);
        var paths = locator.Locate();
        SeedAuthorizedDshUpdateTree(paths.DshCurrentDirectory, "0.1.1-rc.3");
        SeedScriptedNpm(
            paths,
            "console.log(JSON.stringify({ versions: ['0.1.1-rc.4'], 'dist-tags': { latest: '0.1.1-rc.4' } }));");
        var bridge = new RecordingAgentBridgeService();
        var store = new PsxEnvironmentSettingsStore(Path.Combine(workspace.Path, "env"));
        var settings = new PsxEnvironmentSettingsCoordinator(store, bridge);
        using var supervisor = new DshWebRuntimeSupervisor(
            runtime, bridge, Path.Combine(workspace.Path, "supervisor"),
            Path.Combine(workspace.Path, "dsh-workspace"), settings);

        await supervisor.CheckForUpdateAsync();
        Assert.AreEqual("available", LastRuntimeStatus(bridge).GetProperty("updateState").GetString());

        var revision = store.GetSnapshot().Revision;
        store.BumpRevisionForTests();
        Assert.IsGreaterThan(revision, store.GetSnapshot().Revision);
        Assert.IsFalse(settings.SetDshRegistry(DshRegistryDescriptor.OfficialKey).Changed);

        var kept = LastRuntimeStatus(bridge);
        Assert.AreEqual("available", kept.GetProperty("updateState").GetString());
        Assert.AreEqual(1, kept.GetProperty("availableVersions").GetArrayLength());
        Assert.AreEqual("official", kept.GetProperty("catalogRegistryKey").GetString());
    }

    [TestMethod]
    public void Store_ConcurrentWrites_DocumentRemainsValid()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_ConcurrentWrites_DocumentRemainsValid));
        var store = new PsxEnvironmentSettingsStore(workspace.Path);
        Parallel.For(0, 24, i =>
            store.SetDshRegistry(
                i % 2 == 0
                    ? DshRegistryDescriptor.OfficialKey
                    : DshRegistryDescriptor.NpmmirrorKey));

        var snapshot = store.GetSnapshot();
        Assert.IsTrue(DshRegistryDescriptor.TryGet(snapshot.DshRegistry, out _));
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(workspace.Path, "environment.json")));
        Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(DshRegistryDescriptor.TryGet(
            document.RootElement.GetProperty("dshRegistry").GetString(), out _));
    }

    private static DshWebRuntime CreateRuntime(
        RuntimeLocator locator, TestWorkspace workspace, FakeDshLockSource locks) =>
        new(
            locator,
            Path.Combine(workspace.Path, "logs"),
            TimeSpan.FromMinutes(1),
            locks);

    private static string OtherSri() =>
        "sha512-" + Convert.ToBase64String(Enumerable.Repeat((byte)2, 64).ToArray());

    private static JsonElement LastRuntimeStatus(RecordingAgentBridgeService bridge) =>
        bridge.Events.Last(message => message.GetProperty("type").GetString() == "dsh_runtime_status");

    private static void SeedFakeDshTree(string scratch, string version)
    {
        var packageDirectory = Path.Combine(scratch, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(packageDirectory, "lib"));
        File.WriteAllText(Path.Combine(packageDirectory, "lib", "bin.js"), "// fake dsh entry");
        File.WriteAllText(Path.Combine(packageDirectory, "package.json"), $"{{\"version\":\"{version}\"}}");
    }

    private static void SeedAuthorizedDshUpdateTree(string directory, string version)
    {
        SeedFakeDshTree(directory, version);
        File.WriteAllText(
            Path.Combine(directory, "psx-dsh-update.json"),
            $$"""{"package":"@deepseek-ai/dsh","version":"{{version}}","registry":"https://registry.npmjs.org/"}""");
        File.WriteAllText(
            Path.Combine(directory, "package-lock.json"),
            $$"""
            {
              "lockfileVersion": 3,
              "packages": {
                "": { "dependencies": { "@deepseek-ai/dsh": "{{version}}" } },
                "node_modules/@deepseek-ai/dsh": {
                  "version": "{{version}}",
                  "resolved": "https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-{{version}}.tgz",
                  "integrity": "{{DshSri.TestIntegrity}}"
                }
              }
            }
            """);
    }

    private static void SeedMirrorV2UpdateTree(string directory, string version)
    {
        SeedFakeDshTree(directory, version);
        File.WriteAllText(
            Path.Combine(directory, "psx-dsh-update.json"),
            $$"""{"schemaVersion":2,"package":"@deepseek-ai/dsh","version":"{{version}}","registryKey":"npmmirror","registryOrigin":"https://registry.npmmirror.com/","integrity":"{{DshSri.TestIntegrity}}"}""");
        File.WriteAllText(
            Path.Combine(directory, "package-lock.json"),
            $$"""
            {
              "lockfileVersion": 3,
              "packages": {
                "": { "dependencies": { "@deepseek-ai/dsh": "{{version}}" } },
                "node_modules/@deepseek-ai/dsh": {
                  "version": "{{version}}",
                  "resolved": "https://registry.npmmirror.com/@deepseek-ai/dsh/-/dsh-{{version}}.tgz",
                  "integrity": "{{DshSri.TestIntegrity}}"
                }
              }
            }
            """);
    }

    private static void SeedFakeNodeToolchain(RuntimePaths paths)
    {
        var nodeDirectory = Path.Combine(paths.InstallDirectory, "tools", "node");
        Directory.CreateDirectory(Path.Combine(nodeDirectory, "node_modules", "npm", "bin"));
        File.WriteAllText(Path.Combine(nodeDirectory, "node.exe"), "fake");
        File.WriteAllText(Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js"), "fake");
    }

    private static void SeedScriptedNpm(RuntimePaths paths, string script)
    {
        var nodePath = Path.Combine(
            TestWorkspace.RepositoryRoot, "TestResults", "node22", "node-v22.23.1-win-x64", "node.exe");
        if (!File.Exists(nodePath))
            Assert.Inconclusive("Node 22 toolchain is not available under TestResults/node22.");
        var npmDirectory = Path.Combine(paths.InstallDirectory, "tools", "node", "node_modules", "npm", "bin");
        Directory.CreateDirectory(npmDirectory);
        File.Copy(nodePath, Path.Combine(paths.InstallDirectory, "tools", "node", "node.exe"), overwrite: true);
        File.WriteAllText(Path.Combine(npmDirectory, "npm-cli.js"), script);
        Directory.CreateDirectory(paths.DshSeedDirectory);
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, ".npmrc"), "registry=https://registry.npmjs.org/\n");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package.json"), "{}");
        File.WriteAllText(Path.Combine(paths.DshSeedDirectory, "package-lock.json"), "{}");
    }

    /// <summary>Scripted npm that only models the client's real invocation:
    /// `npm ci`. It reads the version from the staged package.json (written
    /// verbatim from the bundled artifact), installs a fake DSH tree, and can
    /// record that it ran or tamper with the lockfile mid-install.</summary>
    private static void SeedCiNpm(
        RuntimePaths paths,
        string? marker = null,
        bool mutateLockOnCi = false,
        string? runs = null)
    {
        var parts = new List<string>
        {
            "const fs = require('fs');",
            "const path = require('path');"
        };
        if (runs != null)
        {
            parts.Add(
                $"fs.appendFileSync({JsonSerializer.Serialize(runs)}, process.argv.join(' ') + '\\n');");
        }
        if (marker != null)
            parts.Add($"fs.writeFileSync({JsonSerializer.Serialize(marker)}, 'ran');");
        parts.Add(
            """
            const version = JSON.parse(fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')).dependencies['@deepseek-ai/dsh'];
            const packageDir = path.join(process.cwd(), 'node_modules', '@deepseek-ai', 'dsh');
            fs.mkdirSync(path.join(packageDir, 'lib'), { recursive: true });
            fs.writeFileSync(path.join(packageDir, 'lib', 'bin.js'), '// staged');
            fs.writeFileSync(path.join(packageDir, 'package.json'), JSON.stringify({ version }));
            """);
        if (mutateLockOnCi)
        {
            parts.Add(
                $$"""
                const lock = JSON.parse(fs.readFileSync(path.join(process.cwd(), 'package-lock.json'), 'utf8'));
                lock.packages['node_modules/@deepseek-ai/dsh'].integrity = '{{OtherSri()}}';
                fs.writeFileSync(path.join(process.cwd(), 'package-lock.json'), JSON.stringify(lock));
                """);
        }
        SeedScriptedNpm(paths, string.Join("\n", parts));
    }

    private sealed class ThrowingLockSource : IDshLockSource
    {
        public DshCatalogSnapshot LoadCatalog() =>
            throw new InvalidOperationException("first install must not read the lock catalog");

        public DshLockLookupResult Find(string version) =>
            throw new InvalidOperationException("first install must not read the lock catalog");
    }
}
