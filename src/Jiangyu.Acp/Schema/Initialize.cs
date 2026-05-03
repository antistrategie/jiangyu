using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class InitializeRequest
{
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; set; }

    [JsonPropertyName("clientCapabilities")]
    public required ClientCapabilities ClientCapabilities { get; set; }

    [JsonPropertyName("clientInfo")]
    public required Implementation ClientInfo { get; set; }
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
}

public sealed class Implementation
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
