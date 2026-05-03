using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class LoadSessionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; set; }

    [JsonPropertyName("mcpServers")]
    public required McpServerConfig[] McpServers { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class LoadSessionResponse
{
    [JsonPropertyName("modes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Modes { get; set; }

    [JsonPropertyName("configOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ConfigOptions { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class CloseSessionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }
}

public sealed class CloseSessionResponse
{
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ListSessionsRequest
{
    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cursor { get; set; }

    [JsonPropertyName("cwd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cwd { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ListSessionsResponse
{
    [JsonPropertyName("sessions")]
    public required SessionSummary[] Sessions { get; set; }

    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; set; }
}

public sealed class SessionSummary
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public sealed class SetConfigOptionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("key")]
    public required string Key { get; set; }

    [JsonPropertyName("value")]
    public required JsonElement Value { get; set; }
}

public sealed class SetConfigOptionResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public sealed class CancelNotification
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }
}

public sealed class SessionNotification
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("update")]
    public required SessionUpdate Update { get; set; }
}
