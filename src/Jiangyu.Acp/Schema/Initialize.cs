using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class InitializeRequest
{
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; set; }

    [JsonPropertyName("clientCapabilities")]
    public required ClientCapabilities ClientCapabilities { get; set; }

    [JsonPropertyName("clientInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Implementation? ClientInfo { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class InitializeResponse
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("agentCapabilities")]
    public AgentCapabilities? AgentCapabilities { get; set; }

    [JsonPropertyName("agentInfo")]
    public Implementation? AgentInfo { get; set; }

    [JsonPropertyName("authMethods")]
    public AuthMethod[]? AuthMethods { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class Implementation
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}
