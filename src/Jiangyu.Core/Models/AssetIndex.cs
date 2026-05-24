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
    /// Sprite-only metadata: backing texture identity plus atlas-region rect
    /// and rotation. Null for non-Sprite entries.
    /// </summary>
    [JsonPropertyName("sprite")]
    public AssetSpriteMetadata? Sprite { get; set; }

    /// <summary>
    /// AudioClip-only metadata: sample rate plus channel count, used by
    /// compile-time validation to confirm a modder-supplied audio file
    /// matches the target's format. Null for non-AudioClip entries.
    /// </summary>
    [JsonPropertyName("audio")]
    public AssetAudioMetadata? Audio { get; set; }

    /// <summary>
    /// SoundBank-only metadata: the resolved bankId plus the names of every
    /// Sound child so modders can target individual sounds as composite
    /// prototype sources. Null for non-SoundBank entries.
    /// </summary>
    [JsonPropertyName("soundBank")]
    public AssetSoundBankMetadata? SoundBank { get; set; }

    /// <summary>
    /// ConversationTemplate-only metadata: the asset's unique <c>Path</c>
    /// (asset names alone aren't unique) plus the role list used by
    /// compile-time RoleGuid resolution and the Studio role picker. Null
    /// for non-ConversationTemplate entries.
    /// </summary>
    [JsonPropertyName("conversation")]
    public AssetConversationMetadata? Conversation { get; set; }
}

/// <summary>
/// Sprite-specific metadata grouped under <see cref="AssetEntry.Sprite"/>.
/// Compile-time atlas detection reads <see cref="BackingTexturePathId"/> +
/// <see cref="BackingTextureCollection"/> to identify atlases (multiple
/// sprites sharing one texture) and the rect/rotation fields to drive
/// per-region compositing.
/// </summary>
public sealed class AssetSpriteMetadata
{
    [JsonPropertyName("backingTexturePathId")]
    public long? BackingTexturePathId { get; set; }

    [JsonPropertyName("backingTextureCollection")]
    public string? BackingTextureCollection { get; set; }

    [JsonPropertyName("backingTextureName")]
    public string? BackingTextureName { get; set; }

    /// <summary>
    /// Atlas-space rectangle (x, y, width, height in pixels) where this
    /// sprite's pixel data lives inside the backing Texture2D. Origin at
    /// bottom-left per Unity convention.
    /// </summary>
    [JsonPropertyName("textureRectX")]
    public float? TextureRectX { get; set; }

    [JsonPropertyName("textureRectY")]
    public float? TextureRectY { get; set; }

    [JsonPropertyName("textureRectWidth")]
    public float? TextureRectWidth { get; set; }

    [JsonPropertyName("textureRectHeight")]
    public float? TextureRectHeight { get; set; }

    /// <summary>
    /// Raw <c>SpritePackingRotation</c> enum value extracted from the
    /// sprite's <c>SettingsRaw</c> field. Non-zero means the sprite region
    /// is stored rotated or flipped in the atlas; the compiler applies the
    /// matching transform before compositing.
    /// </summary>
    [JsonPropertyName("packingRotation")]
    public int? PackingRotation { get; set; }
}

/// <summary>
/// AudioClip-specific metadata grouped under <see cref="AssetEntry.Audio"/>.
/// </summary>
public sealed class AssetAudioMetadata
{
    [JsonPropertyName("frequency")]
    public int? Frequency { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }
}

/// <summary>
/// SoundBank-specific metadata grouped under <see cref="AssetEntry.SoundBank"/>.
/// </summary>
public sealed class AssetSoundBankMetadata
{
    /// <summary>
    /// The asset's serialised <c>bankId</c> from <c>m_Structure.bankId</c>.
    /// Lets the compile-time validator resolve string-keyed <c>Stem.ID.bankId</c>
    /// references to the right int without shipping a hardcoded name→id
    /// table that would drift on every MENACE update.
    /// </summary>
    [JsonPropertyName("bankId")]
    public int? BankId { get; set; }

    /// <summary>
    /// Names of the bank's <c>sounds[]</c> children so modders can target
    /// individual sounds as composite prototype sources via
    /// <c>composite= from="..."</c> authoring.
    /// </summary>
    [JsonPropertyName("namedChildren")]
    public List<string>? NamedChildren { get; set; }
}

/// <summary>
/// ConversationTemplate-specific metadata grouped under
/// <see cref="AssetEntry.Conversation"/>.
/// </summary>
public sealed class AssetConversationMetadata
{
    /// <summary>
    /// The asset's <c>m_Structure.Path</c> value (e.g.
    /// <c>"JeanSy/click_bark"</c>). Path is the unique identifier because
    /// asset names alone aren't (every speaker has its own
    /// <c>click_bark</c>). Powers Studio's from= clone-source dropdown
    /// and the loader's path-keyed lookup.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// The conversation's <c>Roles</c> list flattened to
    /// <c>(RoleName, Guid)</c> pairs. Lets the compile-time validator
    /// resolve string-keyed <c>RoleGuid</c> references on SAY/CHOICE
    /// nodes to the int Guid of the matching role within the same
    /// (possibly cloned) conversation. Also surfaces role names to the
    /// Studio visual editor for combobox population.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<AssetEntryRole>? Roles { get; set; }
}

/// <summary>
/// One <c>Role</c> from a <c>ConversationTemplate</c>'s <c>Roles</c> list.
/// See <see cref="AssetConversationMetadata.Roles"/>.
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
