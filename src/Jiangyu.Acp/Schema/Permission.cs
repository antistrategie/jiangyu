using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class RequestPermissionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("toolCall")]
    public required PermissionToolCall ToolCall { get; set; }

    [JsonPropertyName("options")]
    public required PermissionOption[] Options { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

/// <summary>
/// Snapshot of a tool call, used in permission requests. Same fields as
/// <c>ToolCallUpdate</c> from the spec but without the <c>sessionUpdate</c>
/// discriminator that the session-update envelope carries.
/// </summary>
public sealed class PermissionToolCall
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolCallContent[]? Content { get; set; }

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    [JsonPropertyName("locations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class RequestPermissionResponse
{
    [JsonPropertyName("outcome")]
    public required PermissionOutcome Outcome { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class PermissionOption
{
    [JsonPropertyName("optionId")]
    public required string OptionId { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

/// <summary>
/// Discriminated by the <c>"outcome"</c> property. Values:
/// <c>"cancelled"</c> (empty) and <c>"selected"</c> (has optionId).
/// </summary>
[JsonConverter(typeof(PermissionOutcomeConverter))]
public abstract class PermissionOutcome
{
    [JsonPropertyName("outcome")]
    public abstract string Outcome { get; }
}

public sealed class CancelledPermissionOutcome : PermissionOutcome
{
    public override string Outcome => "cancelled";
}

public sealed class SelectedPermissionOutcome : PermissionOutcome
{
    public override string Outcome => "selected";

    [JsonPropertyName("optionId")]
    public required string OptionId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

internal sealed class PermissionOutcomeConverter : JsonConverter<PermissionOutcome>
{
    public override PermissionOutcome? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var outcome = root.GetProperty("outcome").GetString();
        return outcome switch
        {
            "cancelled" => root.Deserialize<CancelledPermissionOutcome>(options),
            "selected" => root.Deserialize<SelectedPermissionOutcome>(options),
            _ => throw new JsonException($"Unknown PermissionOutcome type: {outcome}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, PermissionOutcome value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
