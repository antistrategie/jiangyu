using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Bundles;

/// <summary>
/// Per-mesh metadata persisted into the compiled <c>jiangyu.json</c>. Drives
/// loader-side bone-name rebinding and material-slot rewires when the
/// runtime applies a mesh replacement.
/// </summary>
public sealed class CompiledMeshMetadata
{
    [JsonPropertyName("boneNames")]
    public string[] BoneNames { get; set; } = Array.Empty<string>();

    [JsonPropertyName("materials")]
    public List<CompiledMaterialBinding>? Materials { get; set; }

    [JsonPropertyName("targetRendererPath")]
    public string? TargetRendererPath { get; set; }

    [JsonPropertyName("targetMeshName")]
    public string? TargetMeshName { get; set; }

    [JsonPropertyName("targetEntityName")]
    public string? TargetEntityName { get; set; }
}

/// <summary>
/// Per-slot material binding. <see cref="Slot"/> is the material array index
/// on the live SMR; <see cref="Textures"/> maps Unity texture-property names
/// (e.g. <c>_MainTex</c>) to the replacement texture's Object.name as it
/// appears in the index.
/// </summary>
public sealed class CompiledMaterialBinding
{
    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("textures")]
    public Dictionary<string, string> Textures { get; set; } = new();
}

/// <summary>
/// One entry inside <c>jiangyu.json</c>'s <c>meshes</c> map. The compiler
/// emits this as either a bare source-ref string or an object with
/// <c>source</c> + compiled metadata; the converter accepts both.
/// </summary>
[JsonConverter(typeof(MeshManifestEntryJsonConverter))]
public sealed class MeshManifestEntry
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("compiled")]
    public CompiledMeshMetadata? Compiled { get; set; }
}

internal sealed class MeshManifestEntryJsonConverter : JsonConverter<MeshManifestEntry>
{
    public override MeshManifestEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var source = reader.GetString() ?? throw new JsonException("Mesh source cannot be null.");
            return new MeshManifestEntry { Source = source };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected string or object for mesh entry.");

        using var doc = JsonDocument.ParseValue(ref reader);
        if (!doc.RootElement.TryGetProperty("source", out var sourceElement) || sourceElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Mesh entry object must contain a string 'source' property.");

        var entry = new MeshManifestEntry
        {
            Source = sourceElement.GetString()!,
        };

        if (doc.RootElement.TryGetProperty("compiled", out var compiledElement) &&
            compiledElement.ValueKind == JsonValueKind.Object)
        {
            entry.Compiled = compiledElement.Deserialize<CompiledMeshMetadata>(options);
        }

        return entry;
    }

    public override void Write(Utf8JsonWriter writer, MeshManifestEntry value, JsonSerializerOptions options)
    {
        if (value.Compiled == null)
        {
            writer.WriteStringValue(value.Source);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("source", value.Source);
        writer.WritePropertyName("compiled");
        JsonSerializer.Serialize(writer, value.Compiled, options);
        writer.WriteEndObject();
    }
}
