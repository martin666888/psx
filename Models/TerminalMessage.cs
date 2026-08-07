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

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("cols")]
    public int? Cols { get; set; }

    [JsonPropertyName("rows")]
    public int? Rows { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("paneId")]
    public string? PaneId { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("providerKey")]
    public string? ProviderKey { get; set; }

    [JsonPropertyName("placement")]
    public string? Placement { get; set; }

    [JsonPropertyName("themeKey")]
    public string? ThemeKey { get; set; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; set; }

    [JsonPropertyName("baseRevision")]
    public long? BaseRevision { get; set; }

    [JsonPropertyName("panes")]
    public List<PaneRatioMessage>? Panes { get; set; }

    [JsonPropertyName("settings")]
    public TerminalOptions? Settings { get; set; }
}

public sealed class PaneRatioMessage
{
    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("ratio")]
    public double Ratio { get; set; }
}
