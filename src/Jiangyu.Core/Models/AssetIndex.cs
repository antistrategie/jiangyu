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

    /// <summary>
    /// For Sprite entries, the atlas-space rectangle (x, y, width, height in pixels) that
    /// defines where this sprite's pixel data lives inside the backing Texture2D. Used by
    /// compile-time atlas compositing to blit modder images into the correct region.
    /// Coordinates follow Unity conventions: origin at bottom-left of the texture.
    /// </summary>
    [JsonPropertyName("spriteTextureRectX")]
    public float? SpriteTextureRectX { get; set; }

    [JsonPropertyName("spriteTextureRectY")]
    public float? SpriteTextureRectY { get; set; }

    [JsonPropertyName("spriteTextureRectWidth")]
    public float? SpriteTextureRectWidth { get; set; }

    [JsonPropertyName("spriteTextureRectHeight")]
    public float? SpriteTextureRectHeight { get; set; }

    /// <summary>
    /// Raw <c>SpritePackingRotation</c> enum value extracted from the sprite's
    /// <c>SettingsRaw</c> field. Non-zero means the sprite region is stored rotated
    /// or flipped in the atlas; the compiler must apply the matching transform to the
    /// modder's image before compositing.
    /// </summary>
    [JsonPropertyName("spritePackingRotation")]
    public int? SpritePackingRotation { get; set; }

    [JsonPropertyName("audioFrequency")]
    public int? AudioFrequency { get; set; }

    [JsonPropertyName("audioChannels")]
    public int? AudioChannels { get; set; }
}

public sealed class IndexManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("gameAssemblyHash")]
    public string? GameAssemblyHash { get; set; }

    [JsonPropertyName("indexedAt")]
    public DateTimeOffset IndexedAt { get; set; }

    [JsonPropertyName("gameDataPath")]
    public string? GameDataPath { get; set; }

    [JsonPropertyName("assetCount")]
    public int AssetCount { get; set; }
}
