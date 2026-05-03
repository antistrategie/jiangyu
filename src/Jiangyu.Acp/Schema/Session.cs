using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class NewSessionRequest
{
    [JsonPropertyName("cwd")]
    public required string Cwd { get; set; }

    [JsonPropertyName("mcpServers")]
    public required McpServerConfig[] McpServers { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class NewSessionResponse
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

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

/// <summary>
/// MCP server descriptor passed to the agent in <c>session/new</c>. The wire
/// shape varies by transport: stdio servers carry <see cref="Command"/> and
/// <see cref="Args"/>; HTTP/SSE servers carry <see cref="Url"/> and
/// <see cref="Headers"/>. Per the spec, HTTP and SSE require the agent to
/// advertise <c>agentCapabilities.mcpCapabilities.http</c> /
/// <c>.sse</c>; stdio is always supported.
/// </summary>
public sealed class McpServerConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    // --- Discriminator for HTTP/SSE transports (stdio has no type field) ---

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    // --- HTTP / SSE transports ---

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HttpHeader[]? Headers { get; set; }

    // --- stdio transport ---

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Args { get; set; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvVariable[]? Env { get; set; }
}

public sealed class HttpHeader
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("value")]
    public required string Value { get; set; }
}

public sealed class EnvVariable
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("value")]
    public required string Value { get; set; }
}
