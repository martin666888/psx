using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Process-wide owner of environment preferences and the
/// <c>app_settings_*</c> bridge. DSH reads already-persisted snapshots from
/// here; it never owns the store.
/// </summary>
public sealed class PsxEnvironmentSettingsCoordinator
{
    private readonly PsxEnvironmentSettingsStore _store;
    private readonly IAgentBridgeService _bridge;

    public PsxEnvironmentSettingsCoordinator(
        PsxEnvironmentSettingsStore store,
        IAgentBridgeService bridge)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>Fired after a successful persist that actually changed dshRegistry.</summary>
    public event EventHandler<PsxEnvironmentSettingsSnapshot>? DshRegistryChanged;

    /// <summary>Fired after a successful persist that actually changed localeMode.</summary>
    public event EventHandler<PsxEnvironmentSettingsSnapshot>? LocaleChanged;

    public PsxEnvironmentSettingsSnapshot GetSnapshot() => _store.GetSnapshot();

    /// <summary>The concrete display language for the persisted mode.</summary>
    public string ResolvedLocale => GetSnapshot().ResolvedLocale;

    /// <summary>
    /// Persist <paramref name="key"/> and return the already-written snapshot.
    /// Callers must use this return value instead of reading the store again.
    /// Same-key sets are no-ops: no revision bump, no change event.
    /// </summary>
    public PsxEnvironmentSetResult SetDshRegistry(string? key)
    {
        var result = _store.SetDshRegistry(key);
        if (result.Success && result.Changed)
            DshRegistryChanged?.Invoke(this, result.Snapshot);
        return result;
    }

    /// <summary>Persist a UI locale mode; same-value sets are no-ops.</summary>
    public PsxEnvironmentSetResult SetLocale(string? mode)
    {
        var result = _store.SetLocale(mode);
        if (result.Success && result.Changed)
            LocaleChanged?.Invoke(this, result.Snapshot);
        return result;
    }

    public Task HandleCommandAsync(string requestId, string action, string? registry, string? localeMode = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return Task.CompletedTask;

        if (action == "get")
            return PublishSnapshotAsync(GetSnapshot(), requestId);

        if (action == "set_locale")
        {
            var result = SetLocale(localeMode);
            if (!result.Success)
            {
                return PublishSnapshotAsync(
                    result.Snapshot,
                    requestId,
                    result.ErrorClass ?? DshErrorClass.SettingsWriteFailed);
            }

            var reply = PublishSnapshotAsync(result.Snapshot, requestId);
            if (!result.Changed)
                return reply;
            return Task.WhenAll(reply, PublishSnapshotAsync(result.Snapshot, null));
        }

        if (action != "set_dsh_registry")
            return Task.CompletedTask;

        var setResult = SetDshRegistry(registry);
        if (!setResult.Success)
        {
            return PublishSnapshotAsync(
                setResult.Snapshot,
                requestId,
                setResult.ErrorClass ?? DshErrorClass.SettingsWriteFailed);
        }

        var setReply = PublishSnapshotAsync(setResult.Snapshot, requestId);
        if (!setResult.Changed)
            return setReply;
        return Task.WhenAll(setReply, PublishSnapshotAsync(setResult.Snapshot, null));
    }

    /// <summary>Broadcast after a DSH CTA successfully changed the registry.</summary>
    public Task PublishBroadcastAsync() =>
        PublishSnapshotAsync(GetSnapshot(), null);

    public Task PublishSnapshotAsync() =>
        PublishSnapshotAsync(GetSnapshot(), null);

    private Task PublishSnapshotAsync(
        PsxEnvironmentSettingsSnapshot snapshot,
        string? requestId,
        string? errorClass = null)
    {
        // Failures carry the fixed errorClass only — display copy lives in
        // the frontend locales, so composed sentences never cross the bridge.
        return _bridge.SendEventAsync(new
        {
            type = "app_settings_snapshot",
            revision = snapshot.Revision,
            dshRegistry = snapshot.DshRegistry,
            // Persisted preference plus the concrete display language it
            // resolves to; the frontend bootstraps and switches on these.
            localeMode = snapshot.LocaleMode,
            resolvedLocale = snapshot.ResolvedLocale,
            requestId,
            errorClass
        });
    }
}
