using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSX.Services;

/// <summary>
/// Machine-local environment preferences under <c>~/.psx/environment.json</c>.
/// Schema is closed: only <c>dshRegistry</c> is persisted; unknown fields are
/// ignored on read. Writes are tmp + replace; the in-memory cache updates
/// only after a successful replace.
/// </summary>
public sealed class PsxEnvironmentSettingsStore
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly string _path;
    private PsxEnvironmentSettingsSnapshot? _cached;

    public PsxEnvironmentSettingsStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _path = Path.Combine(RootDirectory, "environment.json");
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
            var current = _cached ??= ReadFromDisk();
            _cached = current with { Revision = current.Revision + 1 };
        }
    }

    public PsxEnvironmentSettingsSnapshot GetSnapshot()
    {
        lock (_lock)
            return _cached ??= ReadFromDisk();
    }

    public PsxEnvironmentSetResult SetDshRegistry(string? key)
    {
        if (!DshRegistryDescriptor.TryGet(key, out var descriptor))
            return Fail(GetSnapshot(), "下载源无效。");

        lock (_lock)
        {
            var current = _cached ??= ReadFromDisk();
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

    private PsxEnvironmentSettingsSnapshot ReadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
                return PsxEnvironmentSettingsSnapshot.Default;
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != SchemaVersion)
            {
                return PsxEnvironmentSettingsSnapshot.Default;
            }

            string? stored = null;
            if (root.TryGetProperty("dshRegistry", out var registry)
                && registry.ValueKind == JsonValueKind.String)
                stored = registry.GetString();
            var descriptor = DshRegistryDescriptor.ResolveStoredOrOfficial(stored);
            return new PsxEnvironmentSettingsSnapshot(0, descriptor.Key);
        }
        catch (Exception)
        {
            return PsxEnvironmentSettingsSnapshot.Default;
        }
    }

    private void WriteDocument(PsxEnvironmentSettingsSnapshot snapshot)
    {
        Directory.CreateDirectory(RootDirectory);
        var payload = JsonSerializer.Serialize(
            new EnvironmentDocument
            {
                SchemaVersion = SchemaVersion,
                DshRegistry = snapshot.DshRegistry
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
    }
}

public sealed record PsxEnvironmentSettingsSnapshot(long Revision, string DshRegistry)
{
    public static PsxEnvironmentSettingsSnapshot Default { get; } =
        new(0, DshRegistryDescriptor.OfficialKey);

    public DshRegistryDescriptor Registry =>
        DshRegistryDescriptor.ResolveStoredOrOfficial(DshRegistry);
}

public sealed record PsxEnvironmentSetResult(
    bool Success,
    bool Changed,
    PsxEnvironmentSettingsSnapshot Snapshot,
    string? ErrorClass,
    string? ErrorMessage);
