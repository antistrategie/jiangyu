using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class NewSessionRequest
{
    [JsonPropertyName("cwd")]
    public required string Cwd { get; set; }

    [JsonPropertyName("mcpServers")]
    public McpServerConfig[]? McpServers { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class NewSessionResponse
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("modes")]
    public SessionModeState[]? Modes { get; set; }

    [JsonPropertyName("models")]
    public SessionModelState[]? Models { get; set; }
}

public sealed class SessionModeState
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("isCurrent")]
    public bool IsCurrent { get; set; }
}

public sealed class SessionModelState
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("isCurrent")]
    public bool IsCurrent { get; set; }
}

public sealed class McpServerConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Env { get; set; }
}
