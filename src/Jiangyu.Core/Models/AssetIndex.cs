using System.Text.Json.Serialization;

namespace Jiangyu.Core.Models;

public sealed class AssetIndex
{
    [JsonPropertyName("assets")]
    public List<AssetEntry>? Assets { get; set; }

    /// <summary>
    /// Polymorphic-base FQN → set of discriminator strings observed in
    /// vanilla tagged-string fields. The asset indexer samples
    /// <c>m_SerializedNodes</c> / <c>m_SerializedRequirements</c> /
    /// <c>m_SerAction</c> entries during the ConversationTemplate walk
    /// and records each entry's <c>"TYPE|..."</c> prefix here.
    /// Compile-time validation rejects discriminators not in this set
    /// (otherwise the modder picks a form vanilla doesn't accept and
    /// the conversation silently misbehaves at runtime). The visual
    /// editor's discriminator picker draws from the same set so modders
    /// only see forms the game accepts. Null on indexes built before
    /// the field was introduced.
    /// </summary>
    [JsonPropertyName("taggedDiscriminators")]
    public Dictionary<string, List<string>>? TaggedDiscriminators { get; set; }
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

    /// <summary>
    /// For asset types that hold named sub-elements modders can target as
    /// prototype sources via composite= from="..." authoring (currently
    /// Stem.SoundBank's sounds[]), this lists each sub-element's name. The
    /// asset-index build extracts these once so editor autocompletes and
    /// validators can answer "what names are available inside this asset"
    /// without re-running live AssetRipper inspection. Null on assets with
    /// no such surface.
    /// </summary>
    [JsonPropertyName("namedChildren")]
    public List<string>? NamedChildren { get; set; }

    /// <summary>
    /// For <c>Stem.SoundBank</c> entries, the serialised <c>bankId</c> from
    /// the asset's <c>m_Structure.bankId</c> field. Lets the compile-time
    /// validator resolve string-keyed <c>Stem.ID.bankId</c> references to
    /// the right int without shipping a hardcoded name→id table that
    /// would drift on every MENACE update. Null on non-SoundBank entries
    /// and on indexes built before this field was introduced.
    /// </summary>
    [JsonPropertyName("bankId")]
    public int? BankId { get; set; }

    /// <summary>
    /// For <c>ConversationTemplate</c> entries, the conversation's
    /// <c>Roles</c> list flattened to <c>(RoleName, Guid)</c> pairs.
    /// Lets the compile-time validator resolve string-keyed
    /// <c>RoleGuid</c> references on SAY/CHOICE nodes to the int Guid
    /// of the matching role within the same (possibly cloned)
    /// conversation. Also surfaces role names to the Studio visual
    /// editor for combobox population. Null on non-ConversationTemplate
    /// entries and on indexes built before this field was introduced.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<AssetEntryRole>? Roles { get; set; }

    /// <summary>
    /// For <c>ConversationTemplate</c> entries, the asset's
    /// <c>m_Structure.Path</c> value (e.g. <c>"JeanSy/click_bark"</c>).
    /// Path is the unique identifier — asset names alone aren't
    /// (every speaker has its own <c>click_bark</c>). Powers Studio's
    /// from= clone-source dropdown and the loader's path-keyed lookup.
    /// Null on non-ConversationTemplate entries and on indexes built
    /// before this field was introduced.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>
/// One <c>Role</c> from a <c>ConversationTemplate</c>'s <c>Roles</c> list.
/// See <see cref="AssetEntry.Roles"/>.
/// </summary>
public sealed class AssetEntryRole
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("guid")]
    public int Guid { get; set; }
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
