using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

/// <summary>Content within a tool call or tool call update.</summary>
[JsonConverter(typeof(ToolCallContentConverter))]
public abstract class ToolCallContent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>
/// Standard content block wrapper. The <c>type</c> discriminator is
/// <c>"content"</c> and the payload is a <see cref="ContentBlock"/>.
/// </summary>
public sealed class ContentToolCallContent : ToolCallContent
{
    public override string Type => "content";

    [JsonPropertyName("content")]
    public required ContentBlock Content { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class DiffToolCallContent : ToolCallContent
{
    public override string Type => "diff";

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("newText")]
    public required string NewText { get; set; }

    [JsonPropertyName("oldText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldText { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class TerminalToolCallContent : ToolCallContent
{
    public override string Type => "terminal";

    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ToolCallLocation
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Line { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class PlanEntry
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("priority")]
    public required string Priority { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class AvailableCommand
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Input { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

internal sealed class ToolCallContentConverter : JsonConverter<ToolCallContent>
{
    public override ToolCallContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString();
        return type switch
        {
            "content" => root.Deserialize<ContentToolCallContent>(options),
            "diff" => root.Deserialize<DiffToolCallContent>(options),
            "terminal" => root.Deserialize<TerminalToolCallContent>(options),
            _ => throw new JsonException($"Unknown ToolCallContent type: {type}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ToolCallContent value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
