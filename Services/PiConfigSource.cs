using System.IO;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

/// <summary>Reads user configuration only. Credential values are never projected.</summary>
public sealed class PiConfigSource(Func<string?>? directoryResolver = null) : IAgentConfigSource
{
    internal static string? AgentDirectory()
    {
        var value = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(value)) return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".pi", "agent");
        if (value == "~") return home;
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, value[2..]);
        return Path.IsPathFullyQualified(value) ? value : null;
    }

    public AgentProviderConfigReport Collect(CancellationToken cancellationToken)
    {
        var root = (directoryResolver ?? AgentDirectory)();
        var facts = new List<AgentConfigFact>();
        var models = new List<AgentConfigModelEntry>();
        var skills = new List<AgentConfigSkill>();
        var notes = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(root))
            return new("", "", "", AgentProviderConfigReport.Unavailable, [], [], [], [], [AgentConfigNotes.HomeMissing]);
        foreach (var name in new[] { "settings.json", "models.json", "auth.json" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            if (!AgentConfigSanitizer.TryReadBoundedText(path, out var text, out var oversized))
            {
                notes.Add(oversized ? AgentConfigNotes.FileTooLarge : AgentConfigNotes.ParseFailed);
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(text);
                var json = doc.RootElement;
                if (json.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (name == "settings.json" && String(json, "defaultModel") is { } defaultModel)
                {
                    facts.Add(new(AgentConfigFactLabels.DefaultModel, defaultModel));
                }
                if (name == "auth.json")
                    facts.Add(new(AgentConfigFactLabels.SavedCredentialProviders, string.Join(", ", json.EnumerateObject().Select(p => p.Name))));
                if (name == "models.json" && json.TryGetProperty("providers", out var providers))
                {
                    foreach (var provider in providers.EnumerateObject())
                    {
                        if (!provider.Value.TryGetProperty("models", out var entries)) continue;
                        foreach (var model in entries.EnumerateArray())
                        {
                            if (String(model, "id") is not { } id) continue;
                            models.Add(new(provider.Name + "/" + id, String(model, "name"),
                                AgentConfigSanitizer.SanitizeUrl(String(model, "baseUrl") ?? String(provider.Value, "baseUrl"))));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { notes.Add(AgentConfigNotes.ParseFailed); }
        }
        try
        {
            var path = Path.Combine(root, "skills");
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    skills.Add(new(Path.GetFileName(Path.GetDirectoryName(file))!));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { notes.Add(AgentConfigNotes.ScanFailed); }
        return new("", "", "", notes.Count == 0 ? AgentProviderConfigReport.Available : AgentProviderConfigReport.Partial,
            facts, models, [], skills, notes.ToArray());
    }

    private static string? String(JsonElement json, string name) => json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
