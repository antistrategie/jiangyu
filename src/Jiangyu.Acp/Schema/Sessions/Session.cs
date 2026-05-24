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
    public SessionModes? Modes { get; set; }

    [JsonPropertyName("configOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConfigOption[]? ConfigOptions { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

/// <summary>
/// Per ACP spec, the session modes block declares the catalogue of
/// available modes plus the current selection. Some agents (e.g. Claude
/// Agent) advertise modes like "default" / "plan" / "explain"; users
/// switch via slash commands surfaced through availableCommands. Other
/// agents may omit the field entirely.
/// </summary>
public sealed class SessionModes
{
    [JsonPropertyName("currentModeId")]
    public string? CurrentModeId { get; set; }

    [JsonPropertyName("availableModes")]
    public SessionMode[] AvailableModes { get; set; } = [];
}

public sealed class SessionMode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// One agent-tunable knob (model selection, thinking budget, output
/// format, etc.). All fields are optional because real-world agents
/// emit varying shapes — Claude Agent uses <c>type</c>+<c>currentValue</c>
/// without a stable <c>key</c>; some custom agents use <c>id</c>+<c>value</c>.
/// We accept either by exposing both spellings; consumers fall back to
/// whatever's present. UI skips entries with no identifier.
/// </summary>
public sealed class ConfigOption
{
    /// <summary>Stable identifier for set_config_option. Some agents use
    /// <see cref="Id"/> instead.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Alternate spelling of <see cref="Key"/>. Use
    /// <see cref="Identifier"/> in code to read whichever is set.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Free-form. Common values: <c>"boolean"</c>, <c>"enum"</c>,
    /// <c>"select"</c>, <c>"integer"</c>, <c>"string"</c>. UI dispatches
    /// on this; unknown values fall through to a read-only display.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Current value. Shape depends on <see cref="Type"/>.</summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    /// <summary>Alternate spelling of <see cref="Value"/>. Use
    /// <see cref="CurrentValue"/> in code to read whichever is set.</summary>
    [JsonPropertyName("currentValue")]
    public JsonElement? CurrentValueRaw { get; set; }

    /// <summary>For enum-typed options, the allowed values. Copilot's
    /// ACP fork uses <c>options</c>; some specs use <c>choices</c>.
    /// Read both via the <see cref="AllChoices"/> convenience.</summary>
    [JsonPropertyName("options")]
    public ConfigOptionChoice[]? Options { get; set; }

    [JsonPropertyName("choices")]
    public ConfigOptionChoice[]? Choices { get; set; }

    /// <summary>For integer-typed options.</summary>
    [JsonPropertyName("min")]
    public int? Min { get; set; }

    [JsonPropertyName("max")]
    public int? Max { get; set; }

    /// <summary>Optional grouping hint (e.g. "mode" / "model" /
    /// "thought_level" / "permissions"). UI may use it for section
    /// headers but does not depend on it.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>Best-available identifier. Returns <see cref="Key"/> or
    /// <see cref="Id"/>, whichever is set; null when neither is.</summary>
    [JsonIgnore]
    public string? Identifier => Key ?? Id;

    /// <summary>Best-available current value. Returns <see cref="Value"/>
    /// or <see cref="CurrentValueRaw"/>, whichever is set.</summary>
    [JsonIgnore]
    public JsonElement? CurrentValue => Value ?? CurrentValueRaw;

    /// <summary>Best-available choice list. Returns <see cref="Options"/>
    /// or <see cref="Choices"/>, whichever is set.</summary>
    [JsonIgnore]
    public ConfigOptionChoice[]? AllChoices => Options ?? Choices;
}

public sealed class ConfigOptionChoice
{
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    /// <summary>Display label. Copilot uses <c>name</c>; some specs use
    /// <c>label</c>. Read either via <see cref="DisplayLabel"/>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonIgnore]
    public string? DisplayLabel => Name ?? Label;
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
