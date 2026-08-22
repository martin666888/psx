namespace PSX.Services;

/// <summary>
/// Process-wide owner of environment preferences and the
/// <c>app_settings_*</c> bridge. DSH reads already-persisted snapshots from
/// here; it never owns the store.
/// </summary>
public sealed class PsxEnvironmentSettingsCoordinator
{
    public const string SettingsWriteFailedMessage = "无法保存下载源设置，未开始重试。";

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

    public PsxEnvironmentSettingsSnapshot GetSnapshot() => _store.GetSnapshot();

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

    public Task HandleCommandAsync(string requestId, string action, string? registry)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return Task.CompletedTask;

        if (action == "get")
            return PublishSnapshotAsync(GetSnapshot(), requestId, null, null);

        if (action != "set_dsh_registry")
            return Task.CompletedTask;

        var result = SetDshRegistry(registry);
        if (!result.Success)
        {
            return PublishSnapshotAsync(
                result.Snapshot,
                requestId,
                result.ErrorClass ?? DshErrorClass.SettingsWriteFailed,
                result.ErrorMessage ?? SettingsWriteFailedMessage);
        }

        var reply = PublishSnapshotAsync(result.Snapshot, requestId, null, null);
        if (!result.Changed)
            return reply;
        return Task.WhenAll(reply, PublishSnapshotAsync(result.Snapshot, null, null, null));
    }

    /// <summary>Broadcast after a DSH CTA successfully changed the registry.</summary>
    public Task PublishBroadcastAsync() =>
        PublishSnapshotAsync(GetSnapshot(), null, null, null);

    public Task PublishSnapshotAsync() =>
        PublishSnapshotAsync(GetSnapshot(), null, null, null);

    private Task PublishSnapshotAsync(
        PsxEnvironmentSettingsSnapshot snapshot,
        string? requestId,
        string? errorClass,
        string? errorMessage)
    {
        return _bridge.SendEventAsync(new
        {
            type = "app_settings_snapshot",
            revision = snapshot.Revision,
            dshRegistry = snapshot.DshRegistry,
            requestId,
            errorClass,
            errorMessage
        });
    }
}
