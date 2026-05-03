using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class AuthMethod
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class AuthenticateRequest
{
    [JsonPropertyName("authMethod")]
    public required AuthMethod AuthMethod { get; set; }
}

public sealed class AuthenticateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
