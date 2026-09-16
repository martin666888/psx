using System.Text.Json.Serialization;

namespace PSX.Models;

// Immutable, ordered permission content. Null on older transcripts retains the
// Markdown presentation; never recover file edits by parsing rendered Markdown.
public sealed record AgentEditBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text = "",
    [property: JsonPropertyName("path")] string Path = "",
    [property: JsonPropertyName("displayPath")] string DisplayPath = "",
    [property: JsonPropertyName("external")] bool External = false,
    [property: JsonPropertyName("oldText")] string? OldText = null,
    [property: JsonPropertyName("newText")] string NewText = "");
