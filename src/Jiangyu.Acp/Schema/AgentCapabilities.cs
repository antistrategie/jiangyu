using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class AgentCapabilities
{
    [JsonPropertyName("loadSession")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LoadSession { get; set; }

    [JsonPropertyName("promptCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptCapabilities? PromptCapabilities { get; set; }

    [JsonPropertyName("mcpCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpCapabilities? McpCapabilities { get; set; }

    [JsonPropertyName("sessionCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionCapabilities? SessionCapabilities { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class PromptCapabilities
{
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Image { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Audio { get; set; }

    [JsonPropertyName("embeddedContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EmbeddedContext { get; set; }
}

public sealed class McpCapabilities
{
    [JsonPropertyName("http")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Http { get; set; }

    [JsonPropertyName("sse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Sse { get; set; }
}

public sealed class SessionCapabilities
{
    /// <summary>
    /// ACP session capabilities may be <c>null</c> or an object with options.
    /// Use <see cref="JsonElement"/> to accept either form.
    /// </summary>
    [JsonPropertyName("list")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? List { get; set; }

    [JsonPropertyName("resume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Resume { get; set; }

    [JsonPropertyName("close")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Close { get; set; }
}
