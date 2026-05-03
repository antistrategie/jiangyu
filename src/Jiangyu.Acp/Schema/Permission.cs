using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class RequestPermissionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("options")]
    public required PermissionOption[] Options { get; set; }
}

public sealed class RequestPermissionResponse
{
    [JsonPropertyName("outcome")]
    public required PermissionOutcome Outcome { get; set; }
}

public sealed class PermissionOption
{
    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("isDefault")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDefault { get; set; }
}

[JsonConverter(typeof(PermissionOutcomeConverter))]
public abstract class PermissionOutcome
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class AllowedPermissionOutcome : PermissionOutcome
{
    public override string Type => "allowed";

    [JsonPropertyName("optionLabel")]
    public string? OptionLabel { get; set; }
}

public sealed class DeniedPermissionOutcome : PermissionOutcome
{
    public override string Type => "denied";
}

public sealed class DismissedPermissionOutcome : PermissionOutcome
{
    public override string Type => "dismissed";
}

internal sealed class PermissionOutcomeConverter : JsonConverter<PermissionOutcome>
{
    public override PermissionOutcome? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString();
        return type switch
        {
            "allowed" => root.Deserialize<AllowedPermissionOutcome>(options),
            "denied" => root.Deserialize<DeniedPermissionOutcome>(options),
            "dismissed" => root.Deserialize<DismissedPermissionOutcome>(options),
            _ => throw new JsonException($"Unknown PermissionOutcome type: {type}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, PermissionOutcome value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
