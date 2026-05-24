using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Bundles;

/// <summary>
/// Loader-visible slice of the compiled <c>jiangyu.json</c>. Carries the
/// fields the runtime needs to discover, gate, and route per-mod assets;
/// the authoring-side superset (templates, imported prefabs, version
/// metadata) is owned by <c>Jiangyu.Core.Models.ModManifest</c> and is not
/// represented here so the Loader doesn't take a Core dependency. The wire
/// format is identical so the same JSON file deserialises to either type.
/// </summary>
public sealed class LoaderManifest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("depends")]
    public List<string>? Depends { get; set; }

    [JsonPropertyName("meshes")]
    public Dictionary<string, MeshManifestEntry>? Meshes { get; set; }

    [JsonPropertyName("additionPrefabs")]
    public List<string>? AdditionPrefabs { get; set; }

    public static LoaderManifest? FromJson(string json)
        => JsonSerializer.Deserialize<LoaderManifest>(json, Jiangyu.Shared.JsonOptions.PrettyCamel);
}
