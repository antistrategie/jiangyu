using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class ReadTextFileRequest
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("startLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StartLine { get; set; }

    [JsonPropertyName("endLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndLine { get; set; }

    [JsonPropertyName("maxCharacters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCharacters { get; set; }
}

public sealed class ReadTextFileResponse
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class WriteTextFileRequest
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

public sealed class WriteTextFileResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
