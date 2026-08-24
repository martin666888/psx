using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Machine-local environment preferences under <c>~/.psx/environment.json</c>.
/// Schema v2 persists <c>dshRegistry</c> and <c>localeMode</c>; unknown
/// fields are ignored on read and schema-v1 documents are upgraded in place.
/// Writes are tmp + replace; the in-memory cache updates only after a
/// successful replace. The locale fallback (upgrade = zh-Hans, fresh install
/// = system) is injected from <see cref="PsxInstallState"/> at composition.
/// </summary>
public sealed class PsxEnvironmentSettingsStore
{
    private const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly string _fallbackLocaleMode;
    private PsxEnvironmentSettingsSnapshot? _cached;

    public PsxEnvironmentSettingsStore(
        string rootDirectory,
        string fallbackLocaleMode = LocaleDescriptor.FreshInstallDefaultMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _path = Path.Combine(RootDirectory, "environment.json");
        _fallbackLocaleMode = LocaleDescriptor.IsMode(fallbackLocaleMode)
            ? fallbackLocaleMode
            : LocaleDescriptor.System;
    }

    public string RootDirectory { get; }

    /// <summary>
    /// Test hook: bump the in-memory revision without changing
    /// <c>dshRegistry</c>, simulating a future unrelated settings field.
    /// Catalog expiry must key off the registry, not this revision.
    /// </summary>
    internal void BumpRevisionForTests()
    {
        lock (_lock)
        {
            var current = EnsureCache();
            _cached = current with { Revision = current.Revision + 1 };
        }
    }

    public PsxEnvironmentSettingsSnapshot GetSnapshot()
    {
        lock (_lock)
            return EnsureCache();
    }

    public PsxEnvironmentSetResult SetDshRegistry(string? key)
    {
        if (!DshRegistryDescriptor.TryGet(key, out var descriptor))
            return Fail(GetSnapshot(), "下载源无效。");

        lock (_lock)
        {
            var current = EnsureCache();
            if (string.Equals(current.DshRegistry, descriptor.Key, StringComparison.Ordinal))
                return new(true, false, current, null, null);

            var next = current with
            {
                Revision = current.Revision + 1,
                DshRegistry = descriptor.Key
            };
            try
            {
                WriteDocument(next);
            }
            catch (Exception)
            {
                return Fail(current, "无法保存下载源设置，未开始重试。");
            }

            _cached = next;
            return new(true, true, next, null, null);
        }
    }

    /// <summary>Persist a UI locale mode. Same-value sets are no-ops: no
    /// revision bump. Invalid values are rejected without touching disk.</summary>
    public PsxEnvironmentSetResult SetLocale(string? mode)
    {
        if (!LocaleDescriptor.IsMode(mode))
            return Fail(GetSnapshot(), "语言设置无效。");

        lock (_lock)
        {
            var current = EnsureCache();
            if (string.Equals(current.LocaleMode, mode, StringComparison.Ordinal))
                return new(true, false, current, null, null);

            var next = current with
            {
                Revision = current.Revision + 1,
                LocaleMode = mode!
            };
            try
            {
                WriteDocument(next);
            }
            catch (Exception)
            {
                return Fail(current, "无法保存语言设置，未开始重试。");
            }

            _cached = next;
            return new(true, true, next, null, null);
        }
    }

    private PsxEnvironmentSettingsSnapshot EnsureCache()
    {
        if (_cached != null)
            return _cached;
        var (snapshot, migrate) = ReadFromDisk();
        _cached = snapshot;
        // Persist the schema-v2 upgrade (and a fresh install's default) once,
        // so the pinned language choice survives and is inspectable on disk.
        // A corrupt document is left untouched for manual recovery.
        if (migrate)
        {
            try { WriteDocument(_cached); }
            catch (Exception) { /* retry lazily on the next mutation */ }
        }

        return _cached;
    }

    private (PsxEnvironmentSettingsSnapshot Snapshot, bool Migrate) ReadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
                return (DefaultSnapshot(), Migrate: true);
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            var storedSchema = root.TryGetProperty("schemaVersion", out var schema)
                && schema.ValueKind == JsonValueKind.Number
                    ? schema.GetInt32()
                    : -1;
            if (storedSchema is not (1 or 2))
                return (DefaultSnapshot(), Migrate: false);

            string? stored = null;
            if (root.TryGetProperty("dshRegistry", out var registry)
                && registry.ValueKind == JsonValueKind.String)
                stored = registry.GetString();
            var descriptor = DshRegistryDescriptor.ResolveStoredOrOfficial(stored);

            string? storedLocale = null;
            if (root.TryGetProperty("localeMode", out var locale)
                && locale.ValueKind == JsonValueKind.String)
                storedLocale = locale.GetString();

            // A v1 document never carried a language field: it gets the
            // injected default (zh-Hans for an upgrade, system for fresh).
            var localeMode = LocaleDescriptor.IsMode(storedLocale)
                ? storedLocale!
                : _fallbackLocaleMode;
            return (
                new PsxEnvironmentSettingsSnapshot(0, descriptor.Key, localeMode),
                Migrate: storedSchema == 1);
        }
        catch (Exception)
        {
            return (DefaultSnapshot(), Migrate: false);
        }
    }

    private PsxEnvironmentSettingsSnapshot DefaultSnapshot() =>
        new(0, DshRegistryDescriptor.OfficialKey, _fallbackLocaleMode);

    private void WriteDocument(PsxEnvironmentSettingsSnapshot snapshot)
    {
        Directory.CreateDirectory(RootDirectory);
        var payload = JsonSerializer.Serialize(
            new EnvironmentDocument
            {
                SchemaVersion = SchemaVersion,
                DshRegistry = snapshot.DshRegistry,
                LocaleMode = snapshot.LocaleMode
            },
            JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(_path))
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        else
            File.Move(tempPath, _path);
    }

    private static PsxEnvironmentSetResult Fail(PsxEnvironmentSettingsSnapshot snapshot, string message) =>
        new(false, false, snapshot, DshErrorClass.SettingsWriteFailed, message);

    private sealed class EnvironmentDocument
    {
        public int SchemaVersion { get; set; }
        public string DshRegistry { get; set; } = DshRegistryDescriptor.OfficialKey;
        public string LocaleMode { get; set; } = LocaleDescriptor.FreshInstallDefaultMode;
    }
}

public sealed record PsxEnvironmentSettingsSnapshot(long Revision, string DshRegistry, string LocaleMode)
{
    public static PsxEnvironmentSettingsSnapshot Default { get; } =
        new(0, DshRegistryDescriptor.OfficialKey, LocaleDescriptor.FreshInstallDefaultMode);

    public DshRegistryDescriptor Registry =>
        DshRegistryDescriptor.ResolveStoredOrOfficial(DshRegistry);

    /// <summary>The concrete display language this mode resolves to.</summary>
    public string ResolvedLocale => LocaleDescriptor.Resolve(LocaleMode);
}

public sealed record PsxEnvironmentSetResult(
    bool Success,
    bool Changed,
    PsxEnvironmentSettingsSnapshot Snapshot,
    string? ErrorClass,
    string? ErrorMessage);
