using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

public sealed class AgentThreadStore : IAgentThreadStore
{
    private static readonly object FileIoLock = new();
    private static readonly TimeSpan[] FileRetryDelays =
    [
        TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(800)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configPath;
    private readonly string _indexPath;
    private readonly string _threadsDirectory;

    public string RootDirectory { get; }
    public string AttachmentsDirectory { get; }

    public AgentThreadStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".psx"))
    {
    }

    internal AgentThreadStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _configPath = Path.Combine(RootDirectory, "config.json");
        _threadsDirectory = Path.Combine(RootDirectory, "agent", "threads");
        AttachmentsDirectory = Path.Combine(RootDirectory, "agent", "attachments");
        _indexPath = Path.Combine(RootDirectory, "agent", "index.json");
    }

    public AgentThread CreateThread(string workingDirectory)
    {
        EnsureDirectories();
        var now = DateTimeOffset.Now;
        var thread = new AgentThread
        {
            ThreadId = Guid.NewGuid().ToString(),
            Title = BuildTitle(workingDirectory),
            Cwd = workingDirectory,
            CreatedAt = now,
            UpdatedAt = now
        };

        SaveThread(thread);
        SaveLastThread(thread);
        return thread;
    }

    public AgentThread? LoadThread(string threadId)
    {
        var path = GetThreadPath(threadId);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentThread>(ReadTextWithRetry(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AgentThreadSummary> ListThreads()
    {
        EnsureDirectories();
        var index = PruneMissingThreads(LoadIndex());
        var visibleThreads = new List<AgentThreadSummary>();

        foreach (var summary in index.Threads.OrderByDescending(t => t.UpdatedAt))
        {
            var thread = LoadThreadStrict(summary.ThreadId);

            if (IsEmptyDraft(thread))
                continue;

            visibleThreads.Add(new AgentThreadSummary
            {
                ThreadId = thread.ThreadId,
                Title = thread.Title,
                Cwd = thread.Cwd,
                ClaudeSessionId = thread.ClaudeSessionId,
                AcpSessionId = thread.AcpSessionId,
                Provider = thread.Provider,
                UpdatedAt = thread.UpdatedAt
            });
        }

        // Only publish a rewritten index after every existing thread was read
        // successfully. A corrupt or locked file must leave the old index intact.
        index.Threads = visibleThreads;
        SaveIndex(index);
        RepairLastThreadReference(index);
        return visibleThreads;
    }

    public int DeleteEmptyDrafts()
    {
        lock (FileIoLock)
        {
            EnsureDirectories();
            var emptyThreadIds = Directory.EnumerateFiles(_threadsDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(threadId => !string.IsNullOrWhiteSpace(threadId))
                .Where(threadId =>
                {
                    try
                    {
                        var thread = LoadThread(threadId!);
                        return thread != null && IsEmptyDraft(thread);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Cast<string>()
                .ToArray();

            foreach (var threadId in emptyThreadIds)
                DeleteThreadCore(threadId);

            return emptyThreadIds.Length;
        }
    }

    /// <summary>
    /// Read-only projection of every stored thread for usage aggregation.
    /// Enumerates all thread files directly (not the 100-entry index) and never
    /// rewrites the index. Corrupt/unreadable files are skipped and counted so
    /// the caller can report partial completeness.
    /// </summary>
    public AgentThreadUsageSnapshot ReadUsageSnapshot()
    {
        lock (FileIoLock)
        {
            EnsureDirectories();
            var snapshots = new List<AgentUsageThreadSnapshot>();
            var scanned = 0;
            var skipped = 0;

            foreach (var path in Directory.EnumerateFiles(_threadsDirectory, "*.json"))
            {
                scanned++;
                try
                {
                    var thread = ReadUsageProjectionWithRetry(path);
                    if (thread == null)
                    {
                        skipped++;
                        continue;
                    }

                    snapshots.Add(new AgentUsageThreadSnapshot(
                        thread.Provider,
                        thread.ClaudeSessionId,
                        thread.AcpSessionId));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    skipped++;
                }
            }

            return new AgentThreadUsageSnapshot(snapshots, scanned, skipped);
        }
    }

    private AgentThread LoadThreadStrict(string threadId)
    {
        var path = GetThreadPath(threadId);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Agent thread file was not found: {Path.GetFileName(path)}", path);

        try
        {
            return JsonSerializer.Deserialize<AgentThread>(ReadTextWithRetry(path), JsonOptions)
                ?? throw new InvalidDataException($"Agent thread file is empty: {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"Unable to read Agent thread '{Path.GetFileName(path)}': {ex.Message}",
                ex);
        }
    }

    public void SaveThread(AgentThread thread)
    {
        lock (FileIoLock)
        {
            EnsureDirectories();
            thread.UpdatedAt = DateTimeOffset.Now;
            if (string.IsNullOrWhiteSpace(thread.Title))
                thread.Title = BuildTitle(thread.Cwd);

            WriteTextWithRetryAtomic(
                GetThreadPath(thread.ThreadId),
                JsonSerializer.Serialize(thread, JsonOptions),
                "PSX agent thread");
            UpsertIndex(thread);
            SaveLastThread(thread);
        }
    }

    public void DeleteThread(string threadId)
    {
        lock (FileIoLock)
            DeleteThreadCore(threadId);
    }

    private void DeleteThreadCore(string threadId)
    {
        EnsureDirectories();
        var path = GetThreadPath(threadId);
        if (File.Exists(path))
            File.Delete(path);

        DeleteThreadAttachments(threadId);

        var index = LoadIndex();
        index.Threads.RemoveAll(t => t.ThreadId == threadId);
        index = PruneMissingThreads(index);
        SaveIndex(index);

        var config = LoadConfig();
        if (config.Agent.LastThreadId == threadId)
        {
            config.Agent.LastThreadId = index.Threads.OrderByDescending(t => t.UpdatedAt).FirstOrDefault()?.ThreadId;
            SaveConfig(config);
        }
    }

    public void SaveLastThread(AgentThread thread)
    {
        lock (FileIoLock)
        {
            var config = LoadConfig();
            config.Agent.LastThreadId = thread.ThreadId;
            config.Agent.LastWorkingDirectory = thread.Cwd;
            SaveConfig(config);
        }
    }

    public AgentAttachment SaveAttachment(string threadId, string fileName, string mimeType, byte[] data)
    {
        EnsureDirectories();
        var attachmentId = Guid.NewGuid().ToString("N");
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "image" : Path.GetFileName(fileName);
        var storedFileName = attachmentId + ExtensionForMimeType(mimeType);
        var threadDirectory = GetAttachmentThreadDirectory(threadId);
        Directory.CreateDirectory(threadDirectory);

        var filePath = Path.Combine(threadDirectory, storedFileName);
        File.WriteAllBytes(filePath, data);

        var attachment = new AgentAttachment
        {
            Id = attachmentId,
            FileName = safeName,
            MimeType = mimeType,
            Size = data.LongLength,
            Path = filePath,
            Url = BuildAttachmentUrl(threadId, storedFileName),
            Uri = new Uri(filePath).AbsoluteUri,
            CreatedAt = DateTimeOffset.Now
        };

        WriteTextWithRetryAtomic(
            GetAttachmentMetadataPath(threadId, attachmentId),
            JsonSerializer.Serialize(attachment, JsonOptions),
            "PSX agent attachment metadata");
        return attachment;
    }

    public AgentAttachment? LoadAttachment(string threadId, string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(attachmentId))
            return null;

        var metadataPath = GetAttachmentMetadataPath(threadId, attachmentId);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var attachment = JsonSerializer.Deserialize<AgentAttachment>(ReadTextWithRetry(metadataPath), JsonOptions);
            return attachment != null && File.Exists(attachment.Path) ? attachment : null;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AgentAttachment> LoadAttachments(string threadId, IEnumerable<string> attachmentIds)
    {
        return attachmentIds
            .Select(id => LoadAttachment(threadId, id))
            .Where(attachment => attachment != null)
            .Cast<AgentAttachment>()
            .ToList();
    }

    public void DeleteThreadAttachments(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return;

        var directory = GetAttachmentThreadDirectory(threadId);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private void UpsertIndex(AgentThread thread)
    {
        var index = PruneMissingThreads(LoadIndex());
        index.Threads.RemoveAll(t => t.ThreadId == thread.ThreadId);
        index.Threads.Add(new AgentThreadSummary
        {
            ThreadId = thread.ThreadId,
            Title = thread.Title,
            Cwd = thread.Cwd,
            ClaudeSessionId = thread.ClaudeSessionId,
            AcpSessionId = thread.AcpSessionId,
            Provider = thread.Provider,
            UpdatedAt = thread.UpdatedAt
        });
        index.Threads = index.Threads
            .OrderByDescending(t => t.UpdatedAt)
            .Take(100)
            .ToList();
        SaveIndex(index);
    }

    private AgentThreadIndex PruneMissingThreads()
    {
        var index = PruneMissingThreads(LoadIndex());
        SaveIndex(index);
        RepairLastThreadReference(index);
        return index;
    }

    private AgentThreadIndex PruneMissingThreads(AgentThreadIndex index)
    {
        index.Threads = index.Threads
            .Where(t => !string.IsNullOrWhiteSpace(t.ThreadId) && File.Exists(GetThreadPath(t.ThreadId)))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(100)
            .ToList();
        return index;
    }

    private void RepairLastThreadReference(AgentThreadIndex index)
    {
        var config = LoadConfig();
        if (string.IsNullOrWhiteSpace(config.Agent.LastThreadId) || File.Exists(GetThreadPath(config.Agent.LastThreadId)))
            return;

        config.Agent.LastThreadId = index.Threads.FirstOrDefault()?.ThreadId;
        SaveConfig(config);
    }

    private static bool IsEmptyDraft(AgentThread thread)
    {
        return string.IsNullOrWhiteSpace(thread.ClaudeSessionId)
            && string.IsNullOrWhiteSpace(thread.AcpSessionId)
            && thread.Messages.Count == 0;
    }

    private PsxConfig LoadConfig()
    {
        EnsureDirectories();
        if (!File.Exists(_configPath))
        {
            var config = new PsxConfig();
            SaveConfig(config);
            return config;
        }

        try
        {
            return JsonSerializer.Deserialize<PsxConfig>(ReadTextWithRetry(_configPath), JsonOptions) ?? new PsxConfig();
        }
        catch
        {
            return new PsxConfig();
        }
    }

    private void SaveConfig(PsxConfig config)
    {
        EnsureDirectories();
        WriteTextWithRetryAtomic(_configPath, JsonSerializer.Serialize(config, JsonOptions), "PSX config");
    }

    private AgentThreadIndex LoadIndex()
    {
        EnsureDirectories();
        if (!File.Exists(_indexPath))
            return new AgentThreadIndex();

        try
        {
            return JsonSerializer.Deserialize<AgentThreadIndex>(ReadTextWithRetry(_indexPath), JsonOptions)
                ?? throw new InvalidDataException($"Agent thread index is empty: {_indexPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"Unable to read Agent thread index '{Path.GetFileName(_indexPath)}': {ex.Message}",
                ex);
        }
    }

    private void SaveIndex(AgentThreadIndex index)
    {
        EnsureDirectories();
        WriteTextWithRetryAtomic(_indexPath, JsonSerializer.Serialize(index, JsonOptions), "PSX agent thread index");
    }

    private static string ReadTextWithRetry(string path)
    {
        lock (FileIoLock)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (Exception ex) when (IsTransientFileAccessError(ex) && attempt < FileRetryDelays.Length)
                {
                    Thread.Sleep(FileRetryDelays[attempt]);
                }
            }
        }
    }

    private static AgentUsageThreadProjection? ReadUsageProjectionWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                return JsonSerializer.Deserialize<AgentUsageThreadProjection>(stream, JsonOptions);
            }
            catch (Exception ex) when (
                IsTransientFileAccessError(ex) && attempt < FileRetryDelays.Length)
            {
                Thread.Sleep(FileRetryDelays[attempt]);
            }
        }
    }

    private static void WriteTextWithRetryAtomic(string path, string content, string description)
    {
        lock (FileIoLock)
        {
            Exception? lastException = null;

            for (var attempt = 0; attempt <= FileRetryDelays.Length; attempt++)
            {
                var tempPath = BuildTempPath(path);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(tempPath, content);
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (IsTransientFileAccessError(ex))
                {
                    lastException = ex;
                    TryDeleteTempFile(tempPath);

                    if (attempt == FileRetryDelays.Length)
                        break;

                    Thread.Sleep(FileRetryDelays[attempt]);
                }
                catch
                {
                    TryDeleteTempFile(tempPath);
                    throw;
                }
            }

            throw new IOException($"Failed to save {description} after retries: {path}", lastException);
        }
    }

    private static string BuildTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static bool IsTransientFileAccessError(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch { }
    }

    private string GetThreadPath(string threadId)
    {
        return Path.Combine(_threadsDirectory, $"{threadId}.json");
    }

    private string GetAttachmentThreadDirectory(string threadId)
    {
        var safeThreadId = string.Concat(threadId.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        if (string.IsNullOrWhiteSpace(safeThreadId))
            throw new InvalidOperationException("Invalid thread id.");

        return Path.Combine(AttachmentsDirectory, safeThreadId);
    }

    private string GetAttachmentMetadataPath(string threadId, string attachmentId)
    {
        var safeAttachmentId = string.Concat(attachmentId.Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safeAttachmentId))
            throw new InvalidOperationException("Invalid attachment id.");

        return Path.Combine(GetAttachmentThreadDirectory(threadId), safeAttachmentId + ".json");
    }

    private static string BuildAttachmentUrl(string threadId, string storedFileName)
    {
        return "https://psx-attachments.local/"
               + Uri.EscapeDataString(threadId)
               + "/"
               + Uri.EscapeDataString(storedFileName);
    }

    private static string ExtensionForMimeType(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(_threadsDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
    }

    private static string BuildTitle(string workingDirectory)
    {
        var name = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "Agent Chat" : name;
    }

    private sealed class AgentUsageThreadProjection
    {
        public string Provider { get; set; } = "";
        public string? ClaudeSessionId { get; set; }
        public string? AcpSessionId { get; set; }
    }
}
