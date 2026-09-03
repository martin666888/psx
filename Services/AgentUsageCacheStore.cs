using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PSX.Services;

/// <summary>One dedupable usage record: its local day and summed token count.</summary>
public sealed record AgentUsageCacheRecord(string Day, long Tokens);

/// <summary>
/// Per-file incremental parse state for a local usage contributor. The payload
/// never contains paths, session ids, message ids or content — only parser
/// bookkeeping and per-record attribution: <c>Records</c> maps each record's
/// SHA-256 dedup hash to its day and tokens, <c>ResidualDays</c> carries
/// non-dedupable events as day totals, and <c>LineageUnresolved</c> preserves
/// per-file completeness across cache hits. <c>mtimeUtc</c> is UTC ticks.
/// </summary>
public sealed record AgentUsageCacheEntry(
    string ParserVersion,
    string TimezoneId,
    long FileSize,
    long MtimeUtc,
    long ConsumedLength,
    long TailOffset,
    string? TailHash,
    bool Recognized,
    IReadOnlyDictionary<string, AgentUsageCacheRecord> Records,
    IReadOnlyDictionary<string, long> ResidualDays,
    bool LineageUnresolved = false);

/// <summary>
/// Stages cache entries outside the live cache namespace. A staged entry is
/// invisible to <see cref="IAgentUsageCacheStore.Get"/> until
/// <see cref="Commit"/> is called; disposal without a commit discards it.
/// Methods never throw because the cache is always disposable.
/// </summary>
public interface IAgentUsageCacheBatch : IDisposable
{
    void Save(string entryId, AgentUsageCacheEntry entry);

    void Commit();
}

/// <summary>
/// Disposable per-file cache for local usage contributors. Implementations
/// must never throw: any read/write failure degrades to a cache miss, and a
/// miss always falls back to a real scan. Scans are serialized by
/// <see cref="AgentUsageService"/>, so a store has a single writer.
/// </summary>
public interface IAgentUsageCacheStore
{
    AgentUsageCacheEntry? Get(string entryId);

    void Save(string entryId, AgentUsageCacheEntry entry);

    void Delete(string entryId);

    IAgentUsageCacheBatch BeginBatch();
}

public sealed class NullAgentUsageCacheStore : IAgentUsageCacheStore
{
    public static readonly NullAgentUsageCacheStore Instance = new();

    public AgentUsageCacheEntry? Get(string entryId) => null;

    public void Save(string entryId, AgentUsageCacheEntry entry)
    {
    }

    public void Delete(string entryId)
    {
    }

    public IAgentUsageCacheBatch BeginBatch() => NullAgentUsageCacheBatch.Instance;

    private sealed class NullAgentUsageCacheBatch : IAgentUsageCacheBatch
    {
        public static readonly NullAgentUsageCacheBatch Instance = new();

        public void Save(string entryId, AgentUsageCacheEntry entry)
        {
        }

        public void Commit()
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Computes cache entry ids as the SHA-256 hex of the normalized full path
/// (case-normalized), so on-disk cache files never carry a readable path.
/// </summary>
internal static class AgentUsageCacheEntryId
{
    public static string ForPath(string path)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// JSON-per-entry cache under an injected root directory. Reads treat any
/// exception, format, or size anomaly as a miss. Writes are atomic: serialize
/// to a same-directory temp file, then <see cref="File.Replace(string, string, string?)"/>
/// when the target exists, else <see cref="File.Move(string, string)"/>.
/// </summary>
public sealed class FileAgentUsageCacheStore : IAgentUsageCacheStore
{
    private const int MaxEntryBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _rootDirectory;

    public FileAgentUsageCacheStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
    }

    public AgentUsageCacheEntry? Get(string entryId)
    {
        try
        {
            var path = PathFor(entryId);
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxEntryBytes)
                return null;
            var entry = JsonSerializer.Deserialize<AgentUsageCacheEntry>(
                stream, SerializerOptions);
            return IsWellFormed(entry) ? entry : null;
        }
        catch (Exception)
        {
            // The cache is disposable: every read anomaly is a plain miss.
            return null;
        }
    }

    public void Save(string entryId, AgentUsageCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            WriteAtomic(PathFor(entryId), entry);
        }
        catch (Exception)
        {
            // The cache is disposable: a failed write must never break a scan.
        }
    }

    public void Delete(string entryId)
    {
        try
        {
            File.Delete(PathFor(entryId));
        }
        catch (Exception)
        {
            // Stale entries are harmless; deletion is best-effort.
        }
    }

    public IAgentUsageCacheBatch BeginBatch() => new FileWriteBatch(this);

    private string PathFor(string entryId) =>
        Path.Combine(_rootDirectory, Path.GetFileName(entryId) + ".json");

    private static void WriteAtomic(string target, AgentUsageCacheEntry entry)
    {
        string? tempPath = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath, JsonSerializer.Serialize(entry, SerializerOptions));
            if (File.Exists(target))
                File.Replace(tempPath, target, null);
            else
                File.Move(tempPath, target);
            tempPath = null;
        }
        finally
        {
            if (tempPath != null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // Best-effort temp cleanup only.
                }
            }
        }
    }

    private sealed class FileWriteBatch : IAgentUsageCacheBatch
    {
        private readonly FileAgentUsageCacheStore _owner;
        private readonly string _stagingDirectory;
        private readonly HashSet<string> _entryIds = new(StringComparer.Ordinal);
        private bool _finished;

        public FileWriteBatch(FileAgentUsageCacheStore owner)
        {
            _owner = owner;
            _stagingDirectory = Path.Combine(
                owner._rootDirectory, ".batch-" + Guid.NewGuid().ToString("N"));
        }

        public void Save(string entryId, AgentUsageCacheEntry entry)
        {
            if (_finished)
                return;
            try
            {
                Directory.CreateDirectory(_stagingDirectory);
                var safeId = Path.GetFileName(entryId);
                WriteAtomic(Path.Combine(_stagingDirectory, safeId + ".json"), entry);
                _entryIds.Add(safeId);
            }
            catch (Exception)
            {
                // A failed stage is a cache miss after the batch commits.
            }
        }

        public void Commit()
        {
            if (_finished)
                return;
            _finished = true;
            try
            {
                Directory.CreateDirectory(_owner._rootDirectory);
                foreach (var entryId in _entryIds)
                {
                    var staged = Path.Combine(_stagingDirectory, entryId + ".json");
                    if (!File.Exists(staged))
                        continue;
                    var target = _owner.PathFor(entryId);
                    if (File.Exists(target))
                        File.Replace(staged, target, null);
                    else
                        File.Move(staged, target);
                }
            }
            catch (Exception)
            {
                // Every promoted entry is valid after batch validation; a
                // partial cache commit only makes later scans do more work.
            }
            finally
            {
                DeleteStagingDirectory();
            }
        }

        public void Dispose()
        {
            if (_finished)
                return;
            _finished = true;
            DeleteStagingDirectory();
        }

        private void DeleteStagingDirectory()
        {
            try
            {
                if (Directory.Exists(_stagingDirectory))
                    Directory.Delete(_stagingDirectory, recursive: true);
            }
            catch (Exception)
            {
                // Orphaned staging directories are never visible to Get.
            }
        }
    }

    private static bool IsWellFormed(AgentUsageCacheEntry? entry) =>
        entry != null
        && !string.IsNullOrEmpty(entry.ParserVersion)
        && !string.IsNullOrEmpty(entry.TimezoneId)
        && entry.FileSize >= 0
        && entry.ConsumedLength >= 0
        && entry.ConsumedLength <= entry.FileSize
        && entry.TailOffset >= -1
        && (entry.TailOffset >= 0) == (entry.TailHash != null)
        && entry.Records != null
        && entry.Records.All(pair =>
            !string.IsNullOrEmpty(pair.Key)
            && pair.Value != null
            && !string.IsNullOrEmpty(pair.Value.Day)
            && pair.Value.Tokens >= 0)
        && entry.ResidualDays != null
        && entry.ResidualDays.All(pair => pair.Value >= 0);
}
