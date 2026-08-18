using System.IO;
using System.Text.Json;

namespace PSX.Services;

/// <summary>
/// Immutable snapshot of the local user profile shown in the History dock
/// footer and the Usage panel. <see cref="Revision"/> is monotonic across
/// every successful mutation so late profile broadcasts can be discarded.
/// </summary>
public sealed record AgentProfile(string DisplayName, long Revision, string? AvatarDataUrl);

/// <summary>
/// Result of a profile mutation. <see cref="Error"/> is a user-presentable
/// reason; validation failures never throw so the coordinator can reply with
/// an agent_profile error event instead of crashing the command pipeline.
/// </summary>
public sealed record AgentProfileUpdateResult(AgentProfile Profile, string? Error = null)
{
    public bool Success => Error == null;
}

/// <summary>
/// Local user profile store under <c>~/.psx/profile/</c> (display name +
/// avatar). PSX has no account system: the profile is plain user data, so it
/// lives beside the Agent thread store, never in psx.ini. All writes are
/// atomic (temp file + replace) and guarded by a process-wide lock; corrupt
/// JSON falls back to defaults instead of failing the caller.
/// </summary>
public sealed class AgentProfileStore
{
    public const int MaxDisplayNameLength = 32;
    public const int MaxAvatarBytes = BridgeProtocolLimits.AvatarBytes;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _lock = new();
    private readonly string _profilePath;
    private readonly string _avatarPath;

    public string RootDirectory { get; }

    public AgentProfileStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _profilePath = Path.Combine(RootDirectory, "profile.json");
        _avatarPath = Path.Combine(RootDirectory, "avatar.png");
    }

    public AgentProfile GetProfile()
    {
        lock (_lock)
        {
            var document = ReadDocument();
            return new AgentProfile(document.DisplayName, document.Revision, ReadAvatarDataUrl());
        }
    }

    public AgentProfileUpdateResult SetDisplayName(string? displayName)
    {
        lock (_lock)
        {
            var document = ReadDocument();
            document.DisplayName = SanitizeDisplayName(displayName);
            document.Revision++;
            WriteDocument(document);
            return new AgentProfileUpdateResult(
                new AgentProfile(document.DisplayName, document.Revision, ReadAvatarDataUrl()));
        }
    }

    public AgentProfileUpdateResult SetAvatar(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock)
        {
            var current = ReadDocument();
            if (data.Length > MaxAvatarBytes)
            {
                return new AgentProfileUpdateResult(
                    new AgentProfile(current.DisplayName, current.Revision, ReadAvatarDataUrl()),
                    $"Avatar exceeds the {MaxAvatarBytes / 1024} KB limit.");
            }

            if (!HasPngSignature(data))
            {
                return new AgentProfileUpdateResult(
                    new AgentProfile(current.DisplayName, current.Revision, ReadAvatarDataUrl()),
                    "Avatar must be a PNG image.");
            }

            WriteBytesAtomic(_avatarPath, data);
            current.Revision++;
            WriteDocument(current);
            return new AgentProfileUpdateResult(
                new AgentProfile(current.DisplayName, current.Revision, ReadAvatarDataUrl()));
        }
    }

    private static string SanitizeDisplayName(string? displayName)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length == 0)
            name = DefaultDisplayName();
        if (name.Length > MaxDisplayNameLength)
            name = name[..MaxDisplayNameLength];
        return name;
    }

    private static string DefaultDisplayName()
    {
        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName) ? "User" : userName.Trim();
    }

    private static bool HasPngSignature(byte[] data)
    {
        if (data.Length < PngSignature.Length)
            return false;
        for (var index = 0; index < PngSignature.Length; index++)
        {
            if (data[index] != PngSignature[index])
                return false;
        }

        return true;
    }

    private ProfileDocument ReadDocument()
    {
        if (!File.Exists(_profilePath))
            return new ProfileDocument { DisplayName = DefaultDisplayName() };

        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(
                File.ReadAllText(_profilePath), JsonOptions);
            if (document == null || string.IsNullOrWhiteSpace(document.DisplayName))
                return new ProfileDocument { DisplayName = DefaultDisplayName() };
            document.DisplayName = SanitizeDisplayName(document.DisplayName);
            return document;
        }
        catch (JsonException)
        {
            // Corrupt profile.json must never break the footer: fall back to
            // defaults; the next successful mutation rewrites a valid file.
            return new ProfileDocument { DisplayName = DefaultDisplayName() };
        }
        catch (IOException)
        {
            return new ProfileDocument { DisplayName = DefaultDisplayName() };
        }
    }

    private void WriteDocument(ProfileDocument document)
    {
        Directory.CreateDirectory(RootDirectory);
        WriteBytesAtomic(
            _profilePath,
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions)));
    }

    private string? ReadAvatarDataUrl()
    {
        try
        {
            if (!File.Exists(_avatarPath))
                return null;
            var bytes = File.ReadAllBytes(_avatarPath);
            if (bytes.Length == 0 || bytes.Length > MaxAvatarBytes || !HasPngSignature(bytes))
                return null;
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteBytesAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(RootDirectory);
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, content);
        if (File.Exists(path))
            File.Replace(tempPath, path, destinationBackupFileName: null);
        else
            File.Move(tempPath, path);
    }

    private sealed class ProfileDocument
    {
        public string DisplayName { get; set; } = "";
        public long Revision { get; set; }
    }
}
