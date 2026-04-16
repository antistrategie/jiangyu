using System.Text.Json.Serialization;

namespace Jiangyu.Core.Models;

public sealed class AssetIndex
{
    [JsonPropertyName("assets")]
    public List<AssetEntry>? Assets { get; set; }
}

public sealed class AssetEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("canonicalPath")]
    public string? CanonicalPath { get; set; }

    [JsonPropertyName("className")]
    public string? ClassName { get; set; }

    [JsonPropertyName("classId")]
    public int ClassId { get; set; }

    [JsonPropertyName("pathId")]
    public long PathId { get; set; }

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }
}

public sealed class IndexManifest
{
    [JsonPropertyName("gameAssemblyHash")]
    public string? GameAssemblyHash { get; set; }

    [JsonPropertyName("indexedAt")]
    public DateTimeOffset IndexedAt { get; set; }

    [JsonPropertyName("gameDataPath")]
    public string? GameDataPath { get; set; }

    [JsonPropertyName("assetCount")]
    public int AssetCount { get; set; }
}
