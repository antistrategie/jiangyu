using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

/// <summary>
/// Polymorphic session update. The <see cref="UpdateType"/> discriminator
/// (<c>sessionUpdate</c> in the JSON) selects the concrete subtype.
/// </summary>
[JsonConverter(typeof(SessionUpdateConverter))]
public abstract class SessionUpdate
{
    [JsonPropertyName("sessionUpdate")]
    public abstract string UpdateType { get; }
}

public sealed class AgentMessageChunkUpdate : SessionUpdate
{
    public override string UpdateType => "agent_message_chunk";

    [JsonPropertyName("content")]
    public required ContentBlock Content { get; set; }
}

public sealed class UserMessageChunkUpdate : SessionUpdate
{
    public override string UpdateType => "user_message_chunk";

    [JsonPropertyName("content")]
    public required ContentBlock Content { get; set; }
}

public sealed class AgentThoughtChunkUpdate : SessionUpdate
{
    public override string UpdateType => "agent_thought_chunk";

    [JsonPropertyName("content")]
    public required ContentBlock Content { get; set; }
}

public sealed class ToolCallUpdate : SessionUpdate
{
    public override string UpdateType => "tool_call";

    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("content")]
    public ToolCallContent[]? Content { get; set; }

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    [JsonPropertyName("locations")]
    public ToolCallLocation[]? Locations { get; set; }

    [JsonPropertyName("rawInput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RawInput { get; set; }

    [JsonPropertyName("rawOutput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RawOutput { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }
}

public sealed class ToolCallProgressUpdate : SessionUpdate
{
    public override string UpdateType => "tool_call_update";

    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; set; }

    [JsonPropertyName("content")]
    public ToolCallContent[]? Content { get; set; }

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    [JsonPropertyName("locations")]
    public ToolCallLocation[]? Locations { get; set; }

    [JsonPropertyName("rawInput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RawInput { get; set; }

    [JsonPropertyName("rawOutput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RawOutput { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }
}

public sealed class PlanUpdate : SessionUpdate
{
    public override string UpdateType => "plan";

    [JsonPropertyName("entries")]
    public required PlanEntry[] Entries { get; set; }
}

public sealed class AvailableCommandsUpdate : SessionUpdate
{
    public override string UpdateType => "available_commands_update";

    [JsonPropertyName("availableCommands")]
    public required AvailableCommand[] AvailableCommands { get; set; }
}

public sealed class CurrentModeUpdate : SessionUpdate
{
    public override string UpdateType => "current_mode_update";

    [JsonPropertyName("currentModeId")]
    public required string CurrentModeId { get; set; }
}

public sealed class ConfigOptionUpdate : SessionUpdate
{
    public override string UpdateType => "config_option_update";

    [JsonPropertyName("configOptions")]
    public required JsonElement[] ConfigOptions { get; set; }
}

public sealed class SessionInfoUpdate : SessionUpdate
{
    public override string UpdateType => "session_info_update";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }
}

/// <summary>
/// Fallback for SessionUpdate types we don't have a typed wrapper for yet
/// (e.g. <c>usage_update</c> sent by Claude with token counts). The
/// converter routes unknown discriminator values here instead of throwing
/// so the listen loop doesn't drop the rest of the session updates and
/// we don't spam logs every turn.
/// </summary>
public sealed class UnknownSessionUpdate : SessionUpdate
{
    private readonly string _updateType;

    public UnknownSessionUpdate() : this("unknown") { }

    public UnknownSessionUpdate(string updateType)
    {
        _updateType = updateType;
    }

    public override string UpdateType => _updateType;
}

internal sealed class SessionUpdateConverter : JsonConverter<SessionUpdate>
{
    public override SessionUpdate? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("sessionUpdate", out var discriminator))
            throw new JsonException("Missing 'sessionUpdate' discriminator in SessionUpdate.");

        var type = discriminator.GetString();
        return type switch
        {
            "agent_message_chunk" => root.Deserialize<AgentMessageChunkUpdate>(options),
            "user_message_chunk" => root.Deserialize<UserMessageChunkUpdate>(options),
            "agent_thought_chunk" => root.Deserialize<AgentThoughtChunkUpdate>(options),
            "tool_call" => root.Deserialize<ToolCallUpdate>(options),
            "tool_call_update" => root.Deserialize<ToolCallProgressUpdate>(options),
            "plan" => root.Deserialize<PlanUpdate>(options),
            "available_commands_update" => root.Deserialize<AvailableCommandsUpdate>(options),
            "current_mode_update" => root.Deserialize<CurrentModeUpdate>(options),
            "config_option_update" => root.Deserialize<ConfigOptionUpdate>(options),
            "session_info_update" => root.Deserialize<SessionInfoUpdate>(options),
            // ACP keeps growing — agents have already started sending
            // updates we don't have typed wrappers for (e.g. Claude's
            // `usage_update`). Forward as a placeholder so the listen
            // loop doesn't drop the surrounding notification chain.
            null => throw new JsonException("'sessionUpdate' was null"),
            _ => new UnknownSessionUpdate(type),
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionUpdate value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
