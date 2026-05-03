using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class ReadTextFileRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    /// <summary>1-based start line. Read from this line onward.</summary>
    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; set; }

    /// <summary>Maximum number of lines to read from <see cref="Line"/>.</summary>
    [JsonPropertyName("limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; set; }
}

public sealed class ReadTextFileResponse
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

public sealed class WriteTextFileRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

/// <summary>
/// Empty-shape response for fs/write_text_file. The spec returns null;
/// modelled here as an empty object so the client deserialises uniformly.
/// </summary>
public sealed class WriteTextFileResponse
{
}
