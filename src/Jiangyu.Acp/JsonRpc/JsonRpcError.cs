using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.JsonRpc;

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>Optional spec-defined diagnostic payload (often a string
    /// or object with <c>"detail"</c> / <c>"received"</c>). Read as a
    /// raw <see cref="JsonElement"/> so the host can dump it verbatim
    /// when a request fails.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; set; }
}

public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}
