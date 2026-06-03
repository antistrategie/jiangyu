using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Models;

public sealed class ModManifest
{
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
    /// The game's Unity version at compile time, stamped by the compiler. The
    /// loader compares it to the running game so it can warn when a mod was built
    /// against a different game build. Authoring manifests leave it null.
    /// </summary>
    [JsonPropertyName("compiledForUnity")]
    public string? CompiledForUnity { get; set; }

    /// <summary>
    /// Host-game prefab names that the mod's bake outputs reference at
    /// compile time (shaders, materials, avatars, etc.). Compile auto-rips
    /// these from the user's game install into
    /// <c>unity/Assets/Imported/&lt;name&gt;/</c> when missing, so the
    /// committed repo never needs to ship copyrighted host content. Each
    /// entry must resolve unambiguously via the asset index. Once ripped,
    /// the directory is cached locally and skipped on subsequent compiles.
    /// </summary>
    [JsonPropertyName("importedPrefabs")]
    public List<string>? ImportedPrefabs { get; set; }

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

    /// <summary>
    /// Logical names (Unity Object.name) of GameObjects shipped as addition
    /// prefabs under <c>assets/additions/prefabs/&lt;name&gt;.bundle</c>. The
    /// loader uses this list to differentiate "bundled GameObject that should
    /// satisfy <c>asset=</c> lookups" from "bundled GameObject that drives a
    /// mesh replacement". Convention: each addition bundle's filename stem
    /// equals the GameObject's Object.name and equals the entry in this list.
    /// </summary>
    [JsonPropertyName("additionPrefabs")]
    public List<string>? AdditionPrefabs { get; set; }

    public static ModManifest CreateDefault(string name) => new()
    {
        Name = name,
        Depends = ["Jiangyu >= 1.0.0"],
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.PrettyRelaxedEscape);

    public static ModManifest FromJson(string json) =>
        JsonSerializer.Deserialize<ModManifest>(json, JsonOptions.PrettyRelaxedEscape)
        ?? throw new InvalidOperationException("Failed to deserialise jiangyu.json");

    /// <summary>
    /// Load the manifest sitting in <paramref name="directory"/>, or null when it is
    /// absent or unreadable. For callers that treat a missing or malformed manifest
    /// as "no manifest" rather than an error to surface.
    /// </summary>
    public static ModManifest? TryLoad(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return null;
        try { return FromJson(File.ReadAllText(path)); }
        catch { return null; }
    }

    public const string FileName = "jiangyu.json";
}
