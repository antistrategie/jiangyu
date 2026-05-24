using Jiangyu.Core.Models;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Path-first, bare-name-fallback resolution of a ConversationTemplate
/// identifier to its indexed <see cref="AssetConversationMetadata.Roles"/>.
///
/// <para>ConversationTemplate is the one indexed asset whose <c>Object.name</c>
/// is non-unique (every speaker has a <c>click_bark</c>). The unique
/// identifier is the asset's <c>Path</c> field
/// (<c>JeanSy/click_bark</c>, <c>Bog/click_bark</c>, ...). A name-only
/// match would route every <c>JeanSy/X</c> clone's role lookup to
/// whatever speaker's <c>X</c> happened to be first in the index, so
/// Path must win.</para>
///
/// <para>Both the compile-time <see cref="RoleGuidResolver"/> and the
/// editor-side <see cref="TemplateCatalogValidator.ValidateEditorDocument"/>
/// need the same resolution rule. Keeping the rule here avoids the two
/// sites drifting apart silently.</para>
/// </summary>
internal static class ConversationRoleLookup
{
    /// <summary>
    /// Resolve <paramref name="lookupKey"/> (either a Path like
    /// <c>"JeanSy/click_bark"</c> or a bare asset name like
    /// <c>"click_bark"</c>) to the Roles list of the matching
    /// ConversationTemplate. Returns null when nothing matches or the
    /// index is empty.
    /// </summary>
    public static IReadOnlyList<AssetEntryRole>? FindRoles(
        string? lookupKey,
        IReadOnlyList<AssetEntry>? indexedAssets)
    {
        if (string.IsNullOrEmpty(lookupKey)) return null;
        if (indexedAssets is null || indexedAssets.Count == 0) return null;

        AssetEntry? pathMatch = null;
        AssetEntry? nameMatch = null;
        var slash = lookupKey.LastIndexOf('/');
        var bareName = slash >= 0 && slash < lookupKey.Length - 1
            ? lookupKey[(slash + 1)..]
            : lookupKey;

        foreach (var asset in indexedAssets)
        {
            var conv = asset.Conversation;
            if (conv?.Roles is null || conv.Roles.Count == 0) continue;
            if (pathMatch is null && !string.IsNullOrEmpty(conv.Path)
                && string.Equals(conv.Path, lookupKey, StringComparison.Ordinal))
            {
                // Path is unique, no point continuing.
                pathMatch = asset;
                break;
            }
            if (nameMatch is null && string.Equals(asset.Name, bareName, StringComparison.Ordinal))
                nameMatch = asset;
        }
        return pathMatch?.Conversation?.Roles ?? nameMatch?.Conversation?.Roles;
    }
}
