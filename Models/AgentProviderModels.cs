namespace PSX.Models;

public sealed record AgentDescriptor(
    string Key,
    string DisplayName,
    string AssistantName,
    IReadOnlyCollection<string> LegacyKeys);

/// <summary>
/// Data-driven description of the ACP <c>clientCapabilities</c> a provider wants
/// PSX to advertise on <c>initialize</c>. Capability differences between agents
/// (e.g. Kimi opting out of the reverse filesystem bridge) live here, never as
/// provider-name conditionals in the shared session engine.
/// </summary>
public sealed record AcpClientCapabilityProfile
{
    public bool FileSystemReadText { get; init; }
    public bool FileSystemWriteText { get; init; }
    public bool Terminal { get; init; }
    public bool SessionBooleanConfig { get; init; }
    public bool ElicitationFormUrl { get; init; }
    public bool TerminalOutputMeta { get; init; }
}

public sealed class AcpProcessSpec
{
    public required string FileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();
}
