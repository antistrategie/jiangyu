using System.Text.Json.Serialization;
using Jiangyu.Shared;
using Jiangyu.Shared.Bundles;

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
    /// Mods this one refuses to load alongside. Same grammar as <see cref="Depends"/>:
    /// <c>"Name"</c> blocks on any version, <c>"Name &lt; 2.0.0"</c> blocks only the
    /// matching range.
    /// </summary>
    [JsonPropertyName("conflicts")]
    public List<string>? Conflicts { get; set; }

    /// <summary>
    /// The game's Unity version at compile time, stamped by the compiler. The
    /// loader compares it to the running game so it can warn when a mod was built
    /// against a different game build. Authoring manifests leave it null.
    /// </summary>
    [JsonPropertyName("compiledForUnity")]
    public string? CompiledForUnity { get; set; }

    /// <summary>
    /// The Jiangyu toolchain version that built this mod, stamped by the compiler.
    /// The loader warns when it is newer than the installed loader. Authoring
    /// manifests leave it null.
    /// </summary>
    [JsonPropertyName("compiledForJiangyu")]
    public string? CompiledForJiangyu { get; set; }

    /// <summary>
    /// Host-game prefab names that the mod's bake outputs reference at
    /// compile time (shaders, materials, avatars, etc.). Compile auto-rips
    /// these from the user's game install into
    /// <c>unity/Assets/Imported/&lt;name&gt;/</c> when missing, so the
    /// committed repo never needs to ship copyrighted host content. Each
    /// entry must resolve unambiguously via the asset index. Once ripped,
    /// the directory is cached locally and skipped on subsequent compiles.
    /// </summary>
    [JsonPropertyName("imports")]
    public List<string>? Imports { get; set; }

    /// <summary>
    /// Mesh replacement mappings. Key = target skinned-renderer path under the entity root.
    /// </summary>
    [JsonPropertyName("meshes")]
    public Dictionary<string, MeshManifestEntry>? Meshes { get; set; }

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

    // The Jiangyu requirement is stamped at compile time as compiledForJiangyu, not
    // hardcoded here, so a scaffolded mod starts with no dependencies. A modder adds a
    // hard floor (e.g. "Jiangyu >= 1.3.0") to Depends only when they need one.
    public static ModManifest CreateDefault(string name) => new()
    {
        Name = name,
    };

    public string ToJson() => JsonStore.ToJson(this);

    public static ModManifest FromJson(string json) =>
        JsonStore.FromJson<ModManifest>(json)
        ?? throw new InvalidOperationException("Failed to deserialise jiangyu.json");

    /// <summary>
    /// Load the manifest sitting in <paramref name="directory"/>, or null when it is
    /// absent or unreadable. For callers that treat a missing or malformed manifest
    /// as "no manifest" rather than an error to surface.
    /// </summary>
    public static ModManifest? TryLoad(string directory) => JsonStore.TryLoad<ModManifest>(directory, FileName);

    public const string FileName = "jiangyu.json";
}
