using System.Globalization;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class LocaleTests
{
    // ---- LocaleDescriptor resolution --------------------------------------

    [TestMethod]
    public void Resolve_KnownModes_ReturnModeUnchanged()
    {
        Assert.AreEqual(LocaleDescriptor.ZhHans, LocaleDescriptor.Resolve(LocaleDescriptor.ZhHans));
        Assert.AreEqual(LocaleDescriptor.ZhHant, LocaleDescriptor.Resolve(LocaleDescriptor.ZhHant));
        Assert.AreEqual(LocaleDescriptor.En, LocaleDescriptor.Resolve(LocaleDescriptor.En));
        Assert.AreEqual(LocaleDescriptor.Ja, LocaleDescriptor.Resolve(LocaleDescriptor.Ja));
    }

    [TestMethod]
    public void Resolve_SystemOrInvalid_FollowsWindows()
    {
        using var culture = TestUiCulture.Create("zh-CN");
        Assert.AreEqual(LocaleDescriptor.ZhHans, LocaleDescriptor.Resolve(LocaleDescriptor.System));
        Assert.AreEqual(LocaleDescriptor.ZhHans, LocaleDescriptor.Resolve("bogus"));
        Assert.AreEqual(LocaleDescriptor.ZhHans, LocaleDescriptor.Resolve(null));
    }

    [TestMethod]
    public void ResolveSystem_MapsChineseRegionsToScripts()
    {
        using var hans = TestUiCulture.Create("zh-CN");
        Assert.AreEqual(LocaleDescriptor.ZhHans, LocaleDescriptor.ResolveSystem());
        using var sg = TestUiCulture.Create("zh-SG");
        Assert.AreEqual(LocaleDescriptor.ZhHans, LocaleDescriptor.ResolveSystem());
        using var hant = TestUiCulture.Create("zh-TW");
        Assert.AreEqual(LocaleDescriptor.ZhHant, LocaleDescriptor.ResolveSystem());
        using var hk = TestUiCulture.Create("zh-HK");
        Assert.AreEqual(LocaleDescriptor.ZhHant, LocaleDescriptor.ResolveSystem());
    }

    [TestMethod]
    public void ResolveSystem_HantScriptAndCompoundRegions_MapToTraditional()
    {
        using var hantNeutral = TestUiCulture.Create("zh-Hant");
        Assert.AreEqual(LocaleDescriptor.ZhHant, LocaleDescriptor.ResolveSystem());
        using var hantHk = TestUiCulture.Create("zh-Hant-HK");
        Assert.AreEqual(LocaleDescriptor.ZhHant, LocaleDescriptor.ResolveSystem());
        using var twLegacy = TestUiCulture.Create("zh-TW");
        Assert.AreEqual(LocaleDescriptor.ZhHant, LocaleDescriptor.ResolveSystem());
    }

    [TestMethod]
    public void ResolveSystem_MapsJapaneseAndFallsBackToEnglish()
    {
        using var ja = TestUiCulture.Create("ja-JP");
        Assert.AreEqual(LocaleDescriptor.Ja, LocaleDescriptor.ResolveSystem());
        using var fr = TestUiCulture.Create("fr-FR");
        Assert.AreEqual(LocaleDescriptor.En, LocaleDescriptor.ResolveSystem());
    }

    [TestMethod]
    public void IsMode_RejectsUnknownValues()
    {
        Assert.IsTrue(LocaleDescriptor.IsMode(LocaleDescriptor.System));
        Assert.IsFalse(LocaleDescriptor.IsMode("zh"));
        Assert.IsFalse(LocaleDescriptor.IsMode(""));
        Assert.IsFalse(LocaleDescriptor.IsMode(null));
    }

    // ---- environment.json schema v2 ---------------------------------------

    [TestMethod]
    public void Store_FreshInstall_DefaultsLocaleModeToSystem()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_FreshInstall_DefaultsLocaleModeToSystem));
        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode);
        Assert.AreEqual(LocaleDescriptor.System, store.GetSnapshot().LocaleMode);
    }

    [TestMethod]
    public void Store_UpgradeInstall_PinsSimplifiedChinese()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_UpgradeInstall_PinsSimplifiedChinese));
        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.UpgradeDefaultMode);
        Assert.AreEqual(LocaleDescriptor.ZhHans, store.GetSnapshot().LocaleMode);
    }

    [TestMethod]
    public void Store_V1Document_MigratesRegistryAndWritesV2()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_V1Document_MigratesRegistryAndWritesV2));
        var path = Path.Combine(workspace.Path, "environment.json");
        File.WriteAllText(path, """{"schemaVersion":1,"dshRegistry":"npmmirror"}""");

        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.UpgradeDefaultMode);
        var snapshot = store.GetSnapshot();

        Assert.AreEqual(DshRegistryDescriptor.NpmmirrorKey, snapshot.DshRegistry);
        Assert.AreEqual(LocaleDescriptor.ZhHans, snapshot.LocaleMode);

        // The migration is persisted once: the file is schema v2 and carries
        // both fields, so a later read keeps the same effective settings.
        var document = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.AreEqual(2, document.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(DshRegistryDescriptor.NpmmirrorKey, document.GetProperty("dshRegistry").GetString());
        Assert.AreEqual(LocaleDescriptor.ZhHans, document.GetProperty("localeMode").GetString());
        Assert.AreEqual(
            DshRegistryDescriptor.NpmmirrorKey,
            store.GetSnapshot().DshRegistry);
    }

    [TestMethod]
    public void Store_SetLocale_PersistsBumpsRevisionAndResolves()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_SetLocale_PersistsBumpsRevisionAndResolves));
        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode);
        var before = store.GetSnapshot().Revision;

        var result = store.SetLocale(LocaleDescriptor.Ja);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(before + 1, result.Snapshot.Revision);
        Assert.AreEqual(LocaleDescriptor.Ja, result.Snapshot.ResolvedLocale);

        var reread = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode)
            .GetSnapshot();
        Assert.AreEqual(LocaleDescriptor.Ja, reread.LocaleMode);
    }

    [TestMethod]
    public void Store_SameLocale_IsNoOp()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_SameLocale_IsNoOp));
        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode);
        var first = store.SetLocale(LocaleDescriptor.En);
        Assert.IsTrue(first.Changed);
        var revision = first.Snapshot.Revision;

        var again = store.SetLocale(LocaleDescriptor.En);
        Assert.IsTrue(again.Success);
        Assert.IsFalse(again.Changed);
        Assert.AreEqual(revision, again.Snapshot.Revision);
    }

    [TestMethod]
    public void Store_InvalidLocale_FailsWithoutSwitchingOrWriting()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_InvalidLocale_FailsWithoutSwitchingOrWriting));
        var path = Path.Combine(workspace.Path, "environment.json");
        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.UpgradeDefaultMode);
        var before = store.GetSnapshot();
        var diskBefore = File.ReadAllText(path);

        var result = store.SetLocale("klingon");
        Assert.IsFalse(result.Success);
        Assert.AreEqual(before.LocaleMode, result.Snapshot.LocaleMode);
        Assert.AreEqual(diskBefore, File.ReadAllText(path));
    }

    [TestMethod]
    public void Store_CorruptDocument_KeepsFallbackAndDoesNotOverwriteFile()
    {
        using var workspace = TestWorkspace.Create(nameof(Store_CorruptDocument_KeepsFallbackAndDoesNotOverwriteFile));
        var path = Path.Combine(workspace.Path, "environment.json");
        File.WriteAllText(path, "{not-json");
        var store = new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.UpgradeDefaultMode);

        Assert.AreEqual(LocaleDescriptor.ZhHans, store.GetSnapshot().LocaleMode);
        Assert.AreEqual("{not-json", File.ReadAllText(path));
    }

    // ---- coordinator bridge surface ----------------------------------------

    [TestMethod]
    public async Task Coordinator_GetReply_CarriesLocaleFields()
    {
        using var workspace = TestWorkspace.Create(nameof(Coordinator_GetReply_CarriesLocaleFields));
        var bridge = new RecordingAgentBridgeService();
        var coordinator = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode), bridge);

        await coordinator.HandleCommandAsync("req-get", "get", null, null);
        var snapshot = bridge.Events.Single(e => e.GetProperty("type").GetString() == "app_settings_snapshot");
        Assert.AreEqual(LocaleDescriptor.System, snapshot.GetProperty("localeMode").GetString());
        Assert.AreEqual(LocaleDescriptor.ResolveSystem(), snapshot.GetProperty("resolvedLocale").GetString());
    }

    [TestMethod]
    public async Task Coordinator_SetLocale_ReplyCarriesRequestId_BroadcastDoesNot()
    {
        using var workspace = TestWorkspace.Create(nameof(Coordinator_SetLocale_ReplyCarriesRequestId_BroadcastDoesNot));
        var bridge = new RecordingAgentBridgeService();
        var coordinator = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode), bridge);

        await coordinator.HandleCommandAsync("req-locale", "set_locale", null, LocaleDescriptor.ZhHant);
        var snapshots = bridge.Events
            .Where(e => e.GetProperty("type").GetString() == "app_settings_snapshot")
            .ToArray();
        Assert.HasCount(2, snapshots);
        Assert.AreEqual("req-locale", snapshots[0].GetProperty("requestId").GetString());
        Assert.AreEqual(LocaleDescriptor.ZhHant, snapshots[0].GetProperty("resolvedLocale").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshots[1].GetProperty("requestId").ValueKind);
        Assert.AreEqual(LocaleDescriptor.ZhHant, snapshots[1].GetProperty("localeMode").GetString());
    }

    [TestMethod]
    public void Coordinator_SetLocale_FailureKeepsPreviousLanguage()
    {
        using var workspace = TestWorkspace.Create(nameof(Coordinator_SetLocale_FailureKeepsPreviousLanguage));
        var bridge = new RecordingAgentBridgeService();
        var coordinator = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.UpgradeDefaultMode), bridge);
        coordinator.SetLocale(LocaleDescriptor.En);

        // An invalid mode is refused by the store guard: the snapshot keeps
        // the previously persisted language and nothing is broadcast.
        Assert.IsFalse(coordinator.SetLocale("nope").Success);
        Assert.AreEqual(LocaleDescriptor.En, coordinator.GetSnapshot().LocaleMode);
        Assert.IsTrue(bridge.Events.All(
            e => e.GetProperty("type").GetString() != "app_settings_snapshot"));
    }

    [TestMethod]
    public async Task Coordinator_SameLocaleSet_DoesNotBroadcast()
    {
        using var workspace = TestWorkspace.Create(nameof(Coordinator_SameLocaleSet_DoesNotBroadcast));
        var bridge = new RecordingAgentBridgeService();
        var coordinator = new PsxEnvironmentSettingsCoordinator(
            new PsxEnvironmentSettingsStore(workspace.Path, LocaleDescriptor.FreshInstallDefaultMode), bridge);
        coordinator.SetLocale(LocaleDescriptor.En);

        await coordinator.HandleCommandAsync("req-same", "set_locale", null, LocaleDescriptor.En);
        var snapshots = bridge.Events
            .Where(e => e.GetProperty("type").GetString() == "app_settings_snapshot")
            .ToArray();
        // Only the requestId reply — no second broadcast for a no-op change.
        Assert.HasCount(1, snapshots);
        Assert.AreEqual("req-same", snapshots[0].GetProperty("requestId").GetString());
    }

    // ---- bridge parsing -----------------------------------------------------

    [TestMethod]
    public void Parser_SetLocale_AcceptsKnownModes_AndRejectsUnknown()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            """{"type":"app_settings_command","action":"set_locale","requestId":"r1","localeMode":"zh-Hant"}""",
            out var parsed));
        Assert.AreEqual("set_locale", parsed!.AppSettingsCommand!.Action);
        Assert.AreEqual(LocaleDescriptor.ZhHant, parsed.AppSettingsCommand.LocaleMode);

        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            """{"type":"app_settings_command","action":"set_locale","requestId":"r2","localeMode":"klingon"}""",
            out _));
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            """{"type":"app_settings_command","action":"set_locale","requestId":"r3"}""",
            out _));
    }
}

/// <summary>Temporarily swaps the thread UI culture for locale-mapping
/// assertions.</summary>
internal sealed class TestUiCulture : IDisposable
{
    private readonly CultureInfo _previous;

    private TestUiCulture(CultureInfo previous) => _previous = previous;

    public static TestUiCulture Create(string name)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(name);
        return new TestUiCulture(previous);
    }

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}
