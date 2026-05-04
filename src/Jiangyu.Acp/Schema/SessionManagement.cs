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
    public SessionModes? Modes { get; set; }

    [JsonPropertyName("configOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConfigOption[]? ConfigOptions { get; set; }

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

public sealed class SetSessionModeRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("modeId")]
    public required string ModeId { get; set; }
}

public sealed class SetSessionModeResponse
{
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class SetConfigOptionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    /// <summary>Mirrors the <c>id</c> field on the <see cref="ConfigOption"/>
    /// the agent advertises. ACP wire name is <c>configId</c>; sending
    /// anything else produces a JSON-RPC -32602 (Invalid params) with
    /// <c>data._errors.configId</c> set to "expected string, received
    /// undefined".</summary>
    [JsonPropertyName("configId")]
    public required string ConfigId { get; set; }

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
