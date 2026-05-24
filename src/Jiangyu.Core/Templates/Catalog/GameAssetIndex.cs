using Jiangyu.Core.Models;
using Jiangyu.Shared.Replacements;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Compile-time lookup of vanilla game assets by (category, logical name).
/// Wraps Jiangyu's existing <see cref="AssetIndex"/> so the template
/// validator can accept <c>asset="..."</c> references that point at
/// vanilla game assets (Sprites, Textures, GameObjects, etc.) in addition
/// to mod-shipped additions.
///
/// Names are matched case-sensitively against <see cref="AssetEntry.Name"/>;
/// the category filter is applied against <see cref="AssetEntry.ClassName"/>
/// via the same category↔class mapping the additions catalog uses
/// (<c>prefabs</c> ↔ <c>GameObject</c>, <c>textures</c> ↔ <c>Texture2D</c>,
/// etc.). An asset that the runtime's <c>ModAssetResolver</c> Phase 2 can
/// resolve via <c>Resources.FindObjectsOfTypeAll</c> is intended to be
/// queryable here at compile time too.
/// </summary>
public interface IGameAssetIndex
{
    /// <summary>
    /// True if the vanilla game's asset index has an entry with logical
    /// name <paramref name="name"/> whose Unity class matches the given
    /// addition <paramref name="category"/>.
    /// </summary>
    bool Contains(string category, string name);
}

public sealed class GameAssetIndex : IGameAssetIndex
{
    // (category, name) → exists. Built once at construction; queries are
    // O(1) thereafter.
    private readonly HashSet<string> _byCategoryAndName = new(StringComparer.Ordinal);

    public GameAssetIndex(AssetIndex assetIndex)
    {
        if (assetIndex?.Assets == null) return;
        foreach (var entry in assetIndex.Assets)
        {
            var category = ClassNameToCategory(entry.ClassName);
            if (category == null) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;
            _byCategoryAndName.Add(MakeKey(category, entry.Name));
        }
    }

    public bool Contains(string category, string name)
        => !string.IsNullOrEmpty(category) && !string.IsNullOrEmpty(name)
            && _byCategoryAndName.Contains(MakeKey(category, name));

    private static string MakeKey(string category, string name) => category + "\0" + name;

    // ClassName from the asset index maps to one of AssetCategory's constants.
    // Returns null for class names that aren't asset-addition kinds; those
    // assets aren't candidates for asset="..." references on template fields.
    private static string? ClassNameToCategory(string? className) => className switch
    {
        "Sprite" => AssetCategory.Sprites,
        "Texture2D" => AssetCategory.Textures,
        "AudioClip" => AssetCategory.Audio,
        "Material" => AssetCategory.Materials,
        "GameObject" => AssetCategory.Prefabs,
        _ => null,
    };
}
