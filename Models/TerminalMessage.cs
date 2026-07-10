using System.Text.Json.Serialization;

namespace PSX.Models;

public sealed class TerminalMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("cols")]
    public int? Cols { get; set; }

    [JsonPropertyName("rows")]
    public int? Rows { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("settings")]
    public TerminalOptions? Settings { get; set; }
}
