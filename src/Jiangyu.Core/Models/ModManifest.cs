using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Models;

public sealed class ModManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.1.0";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("depends")]
    public List<string>? Depends { get; set; }

    /// <summary>
    /// Mesh replacement mappings. Key = target skinned-renderer path under the entity root.
    /// </summary>
    [JsonPropertyName("meshes")]
    public Dictionary<string, MeshManifestEntry>? Meshes { get; set; }

    /// <summary>
    /// Compiler-owned internal template patch payload. Each entry targets a
    /// named DataTemplate subtype (EntityTemplate by default when templateType
    /// is omitted). Not a stable modder-facing authoring contract.
    /// </summary>
    [JsonPropertyName("templatePatches")]
    public List<CompiledTemplatePatch>? TemplatePatches { get; set; }

    /// <summary>
    /// Template clone directives. Each entry deep-copies a live template by
    /// (templateType, sourceId) and registers the copy under cloneId. Clones
    /// run before patches apply so the cloneId is targetable by subsequent
    /// <see cref="CompiledTemplatePatch"/> entries.
    /// </summary>
    [JsonPropertyName("templateClones")]
    public List<CompiledTemplateClone>? TemplateClones { get; set; }

    public static ModManifest CreateDefault(string name) => new()
    {
        Name = name,
        Depends = ["Jiangyu >= 1.0.0"],
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ModManifest FromJson(string json) =>
        JsonSerializer.Deserialize<ModManifest>(json, JsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialise jiangyu.json");

    public const string FileName = "jiangyu.json";
}

public sealed class CompiledMeshMetadata
{
    [JsonPropertyName("boneNames")]
    public required string[] BoneNames { get; set; }

    [JsonPropertyName("materials")]
    public List<CompiledMaterialBinding>? Materials { get; set; }

    [JsonPropertyName("targetRendererPath")]
    public string? TargetRendererPath { get; set; }

    [JsonPropertyName("targetMeshName")]
    public string? TargetMeshName { get; set; }

    [JsonPropertyName("targetEntityName")]
    public string? TargetEntityName { get; set; }

    [JsonPropertyName("targetEntityPathId")]
    public long? TargetEntityPathId { get; set; }
}

public sealed class CompiledMaterialBinding
{
    [JsonPropertyName("slot")]
    public required int Slot { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("textures")]
    public required Dictionary<string, string> Textures { get; set; }
}

[JsonConverter(typeof(MeshManifestEntryJsonConverter))]
public sealed class MeshManifestEntry
{
    [JsonPropertyName("source")]
    public required string Source { get; set; }

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
