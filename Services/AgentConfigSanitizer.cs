using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PSX.Services;

/// <summary>
/// Shared secret-stripping helpers for every <see cref="IAgentConfigSource"/>.
/// Providers must not hand-roll env/header value reads or URL query retention.
/// </summary>
public static partial class AgentConfigSanitizer
{
    /// <summary>Soft cap for a single config file read (2 MiB).</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024;

    private static readonly Regex LongAlphanumericToken =
        LongAlphanumericTokenRegex();

    /// <summary>
    /// Attempt to read a UTF-8 text file under the soft size cap. When the file
    /// exceeds <see cref="MaxFileBytes"/>, <paramref name="oversized"/> is true
    /// and <paramref name="text"/> is empty — callers must not fall back to an
    /// unbounded read.
    /// </summary>
    public static bool TryReadBoundedText(
        string path,
        out string text,
        out bool oversized)
    {
        text = string.Empty;
        oversized = false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            if (info.Length > MaxFileBytes)
            {
                oversized = true;
                return false;
            }

            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentConfigSanitizer.TryReadBoundedText failed for '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Strip query and userinfo from a URL, keeping scheme + host + path.
    /// Non-absolute or unparseable values return an empty string.
    /// </summary>
    public static string SanitizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return string.Empty;

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        // UriBuilder re-adds default ports; prefer the original port omission.
        if (uri.IsDefaultPort)
            builder.Port = -1;

        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
    }

    /// <summary>
    /// Join a stdio <c>command</c> with args and mask any arg that looks like a
    /// long opaque token (≥32 continuous alphanumeric characters).
    /// </summary>
    public static string SanitizeStdioTarget(string? command, IEnumerable<string>? args)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(command))
            parts.Add(MaskLongTokens(command.Trim()));

        if (args != null)
        {
            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    continue;
                parts.Add(MaskLongTokens(arg.Trim()));
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Extract object property names only — never values. Used for env /
    /// environment / headers / options maps.
    /// </summary>
    public static IReadOnlyList<string> CollectObjectKeys(
        System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return [];

        var keys = new List<string>();
        foreach (var property in element.EnumerateObject())
            keys.Add(property.Name);
        return keys;
    }

    /// <summary>
    /// List immediate child directory names under <paramref name="directory"/>
    /// (skills folders). Missing directories yield an empty list.
    /// </summary>
    public static IReadOnlyList<string> ListChildDirectoryNames(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        try
        {
            return Directory.GetDirectories(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentConfigSanitizer.ListChildDirectoryNames failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// True when a TOML/config line looks like it carries a secret value and
    /// should be skipped during line-level scans (avoids logging raw secrets).
    /// </summary>
    public static bool LooksLikeSecretAssignment(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
            return false;

        var lower = trimmed.ToLowerInvariant();
        return lower.Contains("api_key", StringComparison.Ordinal)
            || lower.Contains("apikey", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal)
            || lower.Contains("secret", StringComparison.Ordinal)
            || lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("authorization", StringComparison.Ordinal);
    }

    public static string MaskLongTokens(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return LongAlphanumericToken.Replace(value, "•••");
    }

    [GeneratedRegex(@"[A-Za-z0-9]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongAlphanumericTokenRegex();
}
