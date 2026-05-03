using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

/// <summary>Content within a tool call update.</summary>
[JsonConverter(typeof(ToolCallContentConverter))]
public abstract class ToolCallContent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class TextToolCallContent : ToolCallContent
{
    public override string Type => "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

public sealed class DiffToolCallContent : ToolCallContent
{
    public override string Type => "diff";

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("diff")]
    public required string Diff { get; set; }
}

public sealed class TerminalToolCallContent : ToolCallContent
{
    public override string Type => "terminal";

    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class ToolCallLocation
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("range")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolCallRange? Range { get; set; }
}

public sealed class ToolCallRange
{
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
}

public sealed class PlanEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }
}

public sealed class AvailableCommand
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inputs")]
    public AvailableCommandInput[]? Inputs { get; set; }
}

public sealed class AvailableCommandInput
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
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
            "text" => root.Deserialize<TextToolCallContent>(options),
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
