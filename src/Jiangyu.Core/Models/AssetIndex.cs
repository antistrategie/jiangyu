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

    /// <summary>
    /// For Sprite entries, identifies the backing Texture2D the sprite is cut from.
    /// Present only when the indexer could resolve the reference; null for older indexes
    /// or for non-Sprite entries. Used by compile-time atlas detection — a sprite whose
    /// backing texture is shared by multiple indexed Sprites is atlas-backed and cannot
    /// be individually replaced via in-place Texture2D mutation.
    /// </summary>
    [JsonPropertyName("spriteBackingTexturePathId")]
    public long? SpriteBackingTexturePathId { get; set; }

    [JsonPropertyName("spriteBackingTextureCollection")]
    public string? SpriteBackingTextureCollection { get; set; }

    [JsonPropertyName("spriteBackingTextureName")]
    public string? SpriteBackingTextureName { get; set; }

    [JsonPropertyName("audioFrequency")]
    public int? AudioFrequency { get; set; }

    [JsonPropertyName("audioChannels")]
    public int? AudioChannels { get; set; }
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
