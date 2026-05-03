using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

/// <summary>
/// One of the authentication methods an agent advertises in its initialize
/// response. Per the ACP spec at agentclientprotocol.com/protocol/schema:
/// {id, name, description?, _meta?}. The earlier {type, url} shape was a
/// guess that worked for some agents (Claude returns no auth methods at
/// all) but failed on Copilot, which sends the spec-correct shape.
/// </summary>
public sealed class AuthMethod
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class AuthenticateRequest
{
    [JsonPropertyName("methodId")]
    public required string MethodId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class AuthenticateResponse
{
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}
