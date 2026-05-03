using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class PromptRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("prompt")]
    public required ContentBlock[] Prompt { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class PromptResponse
{
    [JsonPropertyName("stopReason")]
    public required string StopReason { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

/// <summary>
/// A content block in a prompt message or session update. The
/// <see cref="Type"/> discriminator indicates the shape.
/// </summary>
[JsonConverter(typeof(ContentBlockConverter))]
public abstract class ContentBlock
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class TextContentBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ImageContentBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "image";

    [JsonPropertyName("data")]
    public required string Data { get; set; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }

    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class AudioContentBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "audio";

    [JsonPropertyName("data")]
    public required string Data { get; set; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ResourceLinkContentBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "resource_link";

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("uri")]
    public required string Uri { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

internal sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString();
        return type switch
        {
            "text" => root.Deserialize<TextContentBlock>(options),
            "image" => root.Deserialize<ImageContentBlock>(options),
            "audio" => root.Deserialize<AudioContentBlock>(options),
            "resource_link" => root.Deserialize<ResourceLinkContentBlock>(options),
            _ => throw new JsonException($"Unknown ContentBlock type: {type}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
