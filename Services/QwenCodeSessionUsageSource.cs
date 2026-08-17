using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Exact, PSX-scoped usage source for Qwen Code's durable runtime output
/// (verified against the official v0.21.5 storage layout). The monthly
/// <c>usage/token-usage-YYYY-MM.jsonl</c> records are the token truth; the
/// per-session chat logs under <c>projects/*/chats/&lt;sessionId&gt;.jsonl</c>
/// only prove completeness (chat ⊆ monthly, because auxiliary calls such as
/// auto-titling write monthly rows without a chat assistant record).
/// </summary>
public sealed class QwenCodeSessionUsageSource : IAgentUsageSource
{
    public const string SourceKey = "acp-qwen";
    public const string ParserVersion = "qwen-token-usage-record/v1";

    private const long MaxSettingsBytes = 2 * 1024 * 1024;
    private static readonly Regex MonthlyFilePattern = new(
        @"^token-usage-\d{4}-\d{2}\.jsonl$",
        RegexOptions.CultureInvariant);

    private readonly Func<IReadOnlyList<string>>? _runtimeRootsResolver;
    private readonly Func<QwenRuntimeRootResolution>? _resolutionOverride;

    public QwenCodeSessionUsageSource(Func<IReadOnlyList<string>>? runtimeRootsResolver = null)
    {
        _runtimeRootsResolver = runtimeRootsResolver;
    }

    /// <summary>Test seam for exercising the production resolution branches
    /// (relative runtime dirs, oversized settings) without touching the real
    /// user environment.</summary>
    internal QwenCodeSessionUsageSource(Func<QwenRuntimeRootResolution> resolutionOverride)
    {
        _resolutionOverride = resolutionOverride;
    }

    public string? ResolveSessionId(AgentUsageThreadSnapshot thread) =>
        thread.AcpSessionId;

    public AgentUsageSourceStatus Collect(
        IReadOnlyCollection<string> sessionIds,
        IAgentUsageRecordSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var now = DateTimeOffset.Now;
        var wanted = new HashSet<string>(
            sessionIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var expected = wanted.Count;
        if (expected == 0)
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Available, 0, 0, 0,
                ExpectedSessions: 0, MatchedSessions: 0, ParserVersion, now, Detail: null);
        }

        var resolution = ResolveRoots();
        if (resolution.Roots.Count == 0)
        {
            return new AgentUsageSourceStatus(
                SourceKey, AgentUsageSourceStatus.Unavailable, 0, 0, 0,
                ExpectedSessions: expected, MatchedSessions: 0, ParserVersion, now,
                Detail: "No Qwen runtime root could be resolved.")
            {
                Reasons = OrderReasons(
                    resolution.HasGap
                        ? [AgentUsageGapReason.UnsupportedFormat, AgentUsageGapReason.MissingSessionLogs]
                        : [AgentUsageGapReason.MissingSessionLogs])
            };
        }

        var states = wanted.ToDictionary(
            id => id,
            _ => new SessionState(),
            StringComparer.OrdinalIgnoreCase);
        var probeableSessionIds = new HashSet<string>(
            wanted.Where(IsSafeSessionId),
            StringComparer.OrdinalIgnoreCase);
        var counters = new ScanCounters();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var seenById = new Dictionary<string, AgentUsageRecord>(StringComparer.Ordinal);

        ScanMonthlyFiles(
            resolution.Roots, wanted, states, seenById, sink, counters, cancellationToken);

        var matched = 0;
        foreach (var sessionId in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = states[sessionId];
            if (probeableSessionIds.Contains(sessionId))
                CollectChatCandidates(resolution.Roots, sessionId, state, counters);

            var outcome = ClassifySession(state, counters, cancellationToken);
            if (outcome.Matched)
                matched++;
            foreach (var reason in outcome.Reasons)
                reasons.Add(reason);
        }

        if (counters.SawUnsupportedFormat || resolution.HasGap)
            reasons.Add(AgentUsageGapReason.UnsupportedFormat);
        if (matched < expected)
            reasons.Add(AgentUsageGapReason.UnmatchedSessions);
        if (counters.TraversalFailed || counters.SkippedFiles > 0 || counters.BadLines > 0)
            reasons.Add(AgentUsageGapReason.UnreadableLogs);

        var status = matched == 0
            ? AgentUsageSourceStatus.Unavailable
            : reasons.Count > 0
                ? AgentUsageSourceStatus.Partial
                : AgentUsageSourceStatus.Available;

        return new AgentUsageSourceStatus(
            SourceKey,
            status,
            counters.ScannedFiles,
            counters.SkippedFiles,
            counters.BadLines,
            ExpectedSessions: expected,
            MatchedSessions: matched,
            ParserVersion,
            now,
            Detail: reasons.Count == 0 ? null : $"matched {matched} of {expected} sessions")
        {
            Reasons = OrderReasons(reasons)
        };
    }

    // ---- runtime root resolution ----

    private QwenRuntimeRootResolution ResolveRoots()
    {
        if (_resolutionOverride != null)
            return _resolutionOverride();
        if (_runtimeRootsResolver != null)
            return new QwenRuntimeRootResolution(
                _runtimeRootsResolver(), SkippedRelativeRuntimeDir: false, SettingsNotUsable: false);
        return ResolveRuntimeRoots(Environment.GetEnvironmentVariable, DefaultHome);
    }

    /// <summary>
    /// Production candidate list, mirroring the official runtime-base priority
    /// (pinned ACP context and the per-session ACP context are unknowable from
    /// PSX and skipped): <c>QWEN_RUNTIME_DIR</c>, then the global settings
    /// <c>advanced.runtimeOutputDir</c>, then <c>QWEN_HOME</c> → <c>~/.qwen</c>.
    /// Relative runtime dirs cannot be re-derived without the original process
    /// cwd, so they are skipped and reported so the source never claims
    /// Available against a scanned default directory.
    /// </summary>
    internal static QwenRuntimeRootResolution ResolveRuntimeRoots(
        Func<string, string?> environment,
        Func<string?> homeResolver)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedRelativeRuntimeDir = false;
        var settingsNotUsable = false;

        void AddRoot(string root)
        {
            if (seen.Add(root))
                roots.Add(root);
        }

        var runtimeDir = environment("QWEN_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            var expanded = ExpandTilde(runtimeDir);
            if (Path.IsPathRooted(expanded))
                AddRoot(expanded);
            else
                skippedRelativeRuntimeDir = true;
        }

        var qwenHomeEnv = environment("QWEN_HOME");
        var home = string.IsNullOrWhiteSpace(qwenHomeEnv)
            ? homeResolver()
            : ExpandTilde(qwenHomeEnv);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var settingsDir = TryReadSettingsRuntimeDir(
                Path.Combine(home, "settings.json"), out var settingsProblem);
            if (settingsProblem)
                settingsNotUsable = true;
            if (!string.IsNullOrWhiteSpace(settingsDir))
            {
                var expanded = ExpandTilde(settingsDir);
                if (Path.IsPathRooted(expanded))
                    AddRoot(expanded);
                else
                    skippedRelativeRuntimeDir = true;
            }

            AddRoot(Path.IsPathRooted(home) ? home : Path.GetFullPath(home));
        }

        return new QwenRuntimeRootResolution(
            roots, skippedRelativeRuntimeDir, settingsNotUsable);
    }

    private static string? TryReadSettingsRuntimeDir(string settingsPath, out bool problem)
    {
        problem = false;
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists)
                return null;
            if (info.Length > MaxSettingsBytes)
            {
                problem = true;
                return null;
            }

            using var document = JsonDocument.Parse(
                File.ReadAllText(settingsPath),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            if (!document.RootElement.TryGetProperty("advanced", out var advanced)
                || advanced.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!advanced.TryGetProperty("runtimeOutputDir", out var dir))
                return null;
            if (dir.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(dir.GetString()))
            {
                problem = true;
                return null;
            }

            return dir.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = true;
            return null;
        }
    }

    private static string ExpandTilde(string path)
    {
        if (path == "~")
            return HomeDir ?? path;
        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = HomeDir;
            return home == null ? path : Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static string? HomeDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? DefaultHome()
    {
        var home = HomeDir;
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".qwen");
    }

    // ---- monthly scan ----

    private static void ScanMonthlyFiles(
        IReadOnlyList<string> roots,
        HashSet<string> wanted,
        Dictionary<string, SessionState> states,
        Dictionary<string, AgentUsageRecord> seenById,
        IAgentUsageRecordSink sink,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usageDir = Path.Combine(root, "usage");
            string[] files;
            try
            {
                if (!TryDirectoryExists(usageDir, out var unreadable))
                {
                    if (unreadable)
                        counters.TraversalFailed = true;
                    continue;
                }

                files = Directory.GetFiles(usageDir, "token-usage-*.jsonl");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                counters.TraversalFailed = true;
                continue;
            }

            foreach (var file in files)
            {
                if (!MonthlyFilePattern.IsMatch(Path.GetFileName(file)))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();

                counters.ScannedFiles++;
                try
                {
                    var oversized = BoundedJsonlReader.Read(
                        file,
                        (line, isUnterminatedFinalLine) => ParseMonthlyLine(
                            line,
                            isUnterminatedFinalLine,
                            wanted,
                            states,
                            seenById,
                            sink,
                            counters),
                        cancellationToken);
                    if (oversized > 0)
                        counters.BadLines = checked(counters.BadLines + oversized);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    counters.SkippedFiles++;
                }
            }
        }
    }

    private static void ParseMonthlyLine(
        ReadOnlyMemory<byte> line,
        bool isUnterminatedFinalLine,
        HashSet<string> wanted,
        Dictionary<string, SessionState> states,
        Dictionary<string, AgentUsageRecord> seenById,
        IAgentUsageRecordSink sink,
        ScanCounters counters)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A half-written trailing line from an interrupted append is not a
            // data-quality problem; every other malformed line is.
            if (!isUnterminatedFinalLine)
                counters.BadLines++;
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1)
            {
                counters.BadLines++;
                return;
            }

            if (!root.TryGetProperty("sessionId", out var sessionProperty)
                || sessionProperty.ValueKind != JsonValueKind.String
                || !wanted.Contains(sessionProperty.GetString()!))
            {
                // Rows for sessions PSX did not create are ignored silently.
                return;
            }

            if (!TryParseMonthlyRecord(root, out var record, out var signature))
            {
                counters.BadLines++;
                return;
            }

            if (!root.TryGetProperty("id", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(idProperty.GetString()))
            {
                counters.BadLines++;
                counters.SawUnsupportedFormat = true;
                return;
            }

            var id = idProperty.GetString()!;
            if (seenById.TryGetValue(id, out var existing))
            {
                // The same record may be duplicated across roots; an identical
                // duplicate is fine, a conflicting one is not.
                if (existing != record)
                {
                    counters.BadLines++;
                    counters.SawUnsupportedFormat = true;
                }

                return;
            }

            seenById.Add(id, record);
            var state = states[sessionProperty.GetString()!];
            state.MonthlySignatures.Add(signature);
            sink.Add(record);
        }
    }

    private static bool TryParseMonthlyRecord(
        JsonElement root,
        out AgentUsageRecord record,
        out QwenUsageSignature signature)
    {
        record = null!;
        signature = default;

        var timestamp = root.TryGetProperty("timestamp", out var timestampProperty)
            && timestampProperty.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                timestampProperty.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            ? parsed
            : (DateTimeOffset?)null;
        if (!timestamp.HasValue)
            return false;

        if (!TryGetToken(root, "inputTokens", out var input)
            || !TryGetToken(root, "outputTokens", out var output)
            || !TryGetToken(root, "cachedTokens", out var cached)
            || !TryGetToken(root, "thoughtsTokens", out var thoughts)
            || !TryGetToken(root, "totalTokens", out var total))
        {
            return false;
        }

        long inputOutput;
        long full;
        try
        {
            inputOutput = checked(input + output);
            full = checked(inputOutput + thoughts);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (total != inputOutput && total != full)
            return false;
        if (cached > input)
            return false;

        long inputTokens;
        long outputTokens;
        try
        {
            inputTokens = checked(input - cached);
            outputTokens = total == inputOutput ? output : checked(output + thoughts);
        }
        catch (OverflowException)
        {
            return false;
        }

        var model = root.TryGetProperty("model", out var modelProperty)
            && modelProperty.ValueKind == JsonValueKind.String
            ? modelProperty.GetString() ?? "unknown"
            : "unknown";

        record = new AgentUsageRecord(
            timestamp.Value, model, inputTokens, outputTokens, cached, CacheCreationTokens: 0);
        signature = new QwenUsageSignature(model, input, output, cached, thoughts, total);
        return true;
    }

    // ---- chat cross-check (completeness proof only, no token production) ----

    private static void CollectChatCandidates(
        IReadOnlyList<string> roots,
        string sessionId,
        SessionState state,
        ScanCounters counters)
    {
        foreach (var root in roots)
        {
            var projectsDir = Path.Combine(root, "projects");
            string[] projectDirs;
            try
            {
                if (!TryDirectoryExists(projectsDir, out var unreadable))
                {
                    if (unreadable)
                        counters.TraversalFailed = true;
                    continue;
                }

                projectDirs = Directory.GetDirectories(projectsDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                counters.TraversalFailed = true;
                continue;
            }

            foreach (var projectDir in projectDirs)
            {
                var chatsDir = Path.Combine(projectDir, "chats");
                if (!TryDirectoryExists(chatsDir, out var unreadable))
                {
                    if (unreadable)
                        counters.TraversalFailed = true;
                    continue;
                }

                var chatPath = Path.Combine(chatsDir, sessionId + ".jsonl");
                var sidecarPath = Path.Combine(chatsDir, sessionId + ".runtime.json");
                var chatExists = TryFileExists(chatPath, out var chatUnreadable);
                var sidecarExists = TryFileExists(sidecarPath, out var sidecarUnreadable);
                if (chatUnreadable || sidecarUnreadable)
                    counters.TraversalFailed = true;
                if (chatExists || sidecarExists)
                {
                    state.Candidates.Add(new ChatCandidate(
                        chatPath, sidecarPath, chatExists, sidecarExists));
                }
            }
        }
    }

    private static SessionOutcome ClassifySession(
        SessionState state,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var hasMonthlyRecords = state.MonthlySignatures.Count > 0;

        if (state.Candidates.Count > 1)
        {
            return new SessionOutcome(
                Matched: false,
                Reasons: [AgentUsageGapReason.AmbiguousSessionLogs]);
        }

        if (state.Candidates.Count == 0)
        {
            // Monthly records are positive evidence on their own; a missing
            // chat log cannot disprove them.
            if (hasMonthlyRecords)
                return new SessionOutcome(true, []);

            return new SessionOutcome(
                Matched: false,
                Reasons: [AgentUsageGapReason.MissingSessionLogs]);
        }

        var candidate = state.Candidates[0];
        if (!candidate.ChatExists)
        {
            // Only the sidecar is present: it proves the session ran and
            // recorded nothing (or monthly already carries the truth).
            return new SessionOutcome(true, []);
        }

        var chat = ReadChatSignatures(candidate.ChatPath, counters, cancellationToken);
        if (!chat.Readable)
        {
            return new SessionOutcome(
                Matched: false,
                Reasons: [AgentUsageGapReason.UnreadableLogs]);
        }

        if (ConsumesAll(chat.Signatures, state.MonthlySignatures))
            return new SessionOutcome(true, []);

        // The chat proves usage that the monthly records do not cover; the
        // exact monthly records are still emitted.
        return new SessionOutcome(
            Matched: false,
            Reasons: [AgentUsageGapReason.UnmatchedSessions]);
    }

    private static ChatReadResult ReadChatSignatures(
        string chatPath,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        var signatures = new List<QwenUsageSignature>();
        var corrupt = false;
        counters.ScannedFiles++;
        try
        {
            var oversized = BoundedJsonlReader.Read(
                chatPath,
                (line, isUnterminatedFinalLine) =>
                {
                    if (corrupt)
                        return;
                    if (!TryParseChatSignature(line, isUnterminatedFinalLine, signatures))
                    {
                        corrupt = true;
                        counters.BadLines++;
                    }
                },
                cancellationToken);
            if (oversized > 0)
            {
                corrupt = true;
                counters.BadLines = checked(counters.BadLines + oversized);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.SkippedFiles++;
            return new ChatReadResult(Readable: false, []);
        }

        return new ChatReadResult(Readable: !corrupt, signatures);
    }

    private static bool TryParseChatSignature(
        ReadOnlyMemory<byte> line,
        bool isUnterminatedFinalLine,
        List<QwenUsageSignature> signatures)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return isUnterminatedFinalLine;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String
                || typeProperty.GetString() != "assistant")
            {
                // Non-assistant records carry no usage; missing usageMetadata
                // on an assistant turn is legal (no model call happened).
                return true;
            }

            var model = root.TryGetProperty("model", out var modelProperty)
                && modelProperty.ValueKind == JsonValueKind.String
                ? modelProperty.GetString() ?? "unknown"
                : "unknown";

            if (!root.TryGetProperty("usageMetadata", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            // Official coercion tolerates missing Gemini usage fields as zero.
            if (!TryGetChatToken(usage, "promptTokenCount", out var prompt)
                || !TryGetChatToken(usage, "candidatesTokenCount", out var candidates)
                || !TryGetChatToken(usage, "cachedContentTokenCount", out var cached)
                || !TryGetChatToken(usage, "thoughtsTokenCount", out var thoughts)
                || !TryGetChatToken(usage, "totalTokenCount", out var total))
            {
                return false;
            }

            signatures.Add(new QwenUsageSignature(
                model, prompt, candidates, cached, thoughts, total));
            return true;
        }
    }

    private static bool ConsumesAll(
        IReadOnlyList<QwenUsageSignature> chatSignatures,
        IReadOnlyList<QwenUsageSignature> monthlySignatures)
    {
        var remaining = new List<QwenUsageSignature>(monthlySignatures);
        foreach (var chatSignature in chatSignatures)
        {
            var index = remaining.FindIndex(candidate => candidate == chatSignature);
            if (index < 0)
                return false;
            remaining.RemoveAt(index);
        }

        return true;
    }

    // ---- shared helpers ----

    private static bool TryGetToken(JsonElement element, string propertyName, out long number)
    {
        number = 0;
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number)
            && number >= 0;
    }

    private static bool TryGetChatToken(JsonElement usage, string propertyName, out long number)
    {
        number = 0;
        if (!usage.TryGetProperty(propertyName, out var value))
            return true;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number)
            && number >= 0;
    }

    private static bool TryDirectoryExists(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return false;
        }
    }

    private static bool TryFileExists(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return false;
        }
    }

    private static bool IsSafeSessionId(string sessionId) =>
        sessionId.Length > 0
        && sessionId != "."
        && sessionId != ".."
        && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !sessionId.Contains(Path.DirectorySeparatorChar)
        && !sessionId.Contains(Path.AltDirectorySeparatorChar);

    private static IReadOnlyList<string> OrderReasons(IEnumerable<string> reasons)
    {
        var set = new HashSet<string>(reasons, StringComparer.Ordinal);
        return AgentUsageGapReason.Ordered.Where(set.Contains).ToArray();
    }

    private sealed class ScanCounters
    {
        public int ScannedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int BadLines { get; set; }
        public bool TraversalFailed { get; set; }
        public bool SawUnsupportedFormat { get; set; }
    }

    private sealed class SessionState
    {
        public List<QwenUsageSignature> MonthlySignatures { get; } = [];
        public List<ChatCandidate> Candidates { get; } = [];
    }

    private sealed record ChatCandidate(
        string ChatPath,
        string SidecarPath,
        bool ChatExists,
        bool SidecarExists);

    private sealed record ChatReadResult(
        bool Readable,
        List<QwenUsageSignature> Signatures);

    private sealed record SessionOutcome(
        bool Matched,
        IReadOnlyList<string> Reasons);

    private readonly record struct QwenUsageSignature(
        string Model,
        long Input,
        long Output,
        long Cached,
        long Thoughts,
        long Total);
}

/// <summary>
/// Result of resolving the production Qwen runtime-root candidates. Carries
/// whether a relative runtime dir was skipped (its original process cwd is
/// unrecoverable) or the global settings could not be read, so the source can
/// record <c>unsupported_format</c> instead of falsely claiming Available.
/// </summary>
internal sealed record QwenRuntimeRootResolution(
    IReadOnlyList<string> Roots,
    bool SkippedRelativeRuntimeDir,
    bool SettingsNotUsable)
{
    public bool HasGap => SkippedRelativeRuntimeDir || SettingsNotUsable;
}
