namespace PSX.Models;

public sealed record AgentDescriptor(
    string Key,
    string DisplayName,
    string AssistantName,
    IReadOnlyCollection<string> LegacyKeys);

public sealed class AcpProcessSpec
{
    public required string FileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();
}
