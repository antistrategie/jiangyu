using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class PromptRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("content")]
    public required ContentBlock[] Content { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class PromptResponse
{
    [JsonPropertyName("stopReason")]
    public required string StopReason { get; set; }
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
}

public sealed class ImageContentBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "image";

    [JsonPropertyName("data")]
    public required string Data { get; set; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }
}

public sealed class ResourceContentBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "resource";

    [JsonPropertyName("resource")]
    public required EmbeddedResource Resource { get; set; }
}

public sealed class EmbeddedResource
{
    [JsonPropertyName("uri")]
    public required string Uri { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("blob")]
    public string? Blob { get; set; }
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
            "resource" => root.Deserialize<ResourceContentBlock>(options),
            _ => throw new JsonException($"Unknown ContentBlock type: {type}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
