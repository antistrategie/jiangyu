using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Bundles;

/// <summary>
/// Loader-visible slice of the compiled <c>jiangyu.json</c>. Carries the
/// fields the runtime needs to discover, gate, and route per-mod assets;
/// the authoring-side superset (templates, imported prefabs) is owned by
/// <c>Jiangyu.Core.Models.ModManifest</c> and is not represented here so the
/// Loader doesn't take a Core dependency. The wire format is identical so the
/// same JSON file deserialises to either type.
/// </summary>
public sealed class LoaderManifest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("depends")]
    public List<string>? Depends { get; set; }

    [JsonPropertyName("conflicts")]
    public List<string>? Conflicts { get; set; }

    /// <summary>The game's Unity version when the mod was compiled, stamped by the
    /// compiler. Null on hand-written manifests. The loader warns when it differs
    /// from the running game.</summary>
    [JsonPropertyName("compiledForUnity")]
    public string? CompiledForUnity { get; set; }

    /// <summary>The Jiangyu toolchain version that compiled the mod, stamped by the
    /// compiler. Null on hand-written manifests. The loader warns when it is newer
    /// than the installed loader.</summary>
    [JsonPropertyName("compiledForJiangyu")]
    public string? CompiledForJiangyu { get; set; }

    [JsonPropertyName("meshes")]
    public Dictionary<string, MeshManifestEntry>? Meshes { get; set; }

    [JsonPropertyName("additionPrefabs")]
    public List<string>? AdditionPrefabs { get; set; }

    public static LoaderManifest? FromJson(string json)
        => JsonSerializer.Deserialize<LoaderManifest>(json, Jiangyu.Shared.JsonOptions.PrettyCamel);

    /// <summary>Read and parse a mod folder's <c>jiangyu.json</c>. Returns false when
    /// it is absent or unreadable, so callers reading a single field share one path.</summary>
    public static bool TryRead(string modFolder, out LoaderManifest? manifest)
    {
        manifest = null;
        var path = Path.Combine(modFolder, "jiangyu.json");
        if (!File.Exists(path))
            return false;
        try { manifest = FromJson(File.ReadAllText(path)); }
        catch { return false; }
        return manifest != null;
    }
}
