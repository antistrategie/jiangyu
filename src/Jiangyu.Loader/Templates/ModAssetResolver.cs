using Il2CppInterop.Runtime;
using Jiangyu.Loader.Bundles;
using UnityEngine;

namespace Jiangyu.Loader.Templates;

// JIANGYU-CONTRACT: Asset references inside template patches and clones
// resolve in two phases at apply time:
//   (1) the loaded mod-bundle catalog (BundleReplacementCatalog): additions
//       packed under assets/additions/<category>/ and replacement files under
//       assets/replacements/<category>/ both land in this dictionary keyed by
//       the Unity Object's name. Bundle entries win because a modder shipping
//       an asset with a name colliding with a vanilla asset is expressing
//       explicit intent to use theirs.
//   (2) the live game asset registry via Resources.FindObjectsOfTypeAll
//       filtered by Unity Object name, so a clone can point at a vanilla
//       asset (e.g. a stock icon) without the modder having to ship a copy.
//
// Scope: derived from the asset-addition design discussion 2026-05-06; the
// supported destination types match AssetCategory.IsSupported (Sprite,
// Texture2D, AudioClip, Material). Mesh and GameObject categories defer to
// the prefab-construction layer; see PREFAB_CLONING_TODO.md.
internal sealed class ModAssetResolver
{
    private readonly BundleReplacementCatalog _bundles;

    public ModAssetResolver(BundleReplacementCatalog bundles)
    {
        _bundles = bundles;
    }

    public UnityEngine.Object TryFind(System.Type unityType, string name)
    {
        if (string.IsNullOrEmpty(name) || unityType == null)
            return null;

        // The bundle stores additions under the flattened-slash name
        // (`__` in place of `/`); modder-facing references keep slashes.
        // Translate before lookup so both layers stay consistent. Game
        // assets in the runtime registry don't carry slashes, so the
        // translation is a no-op for replacement-style names.
        var bundleKey = Jiangyu.Shared.Replacements.AssetCategory.ToBundleAssetName(name);

        // Phase 1: bundle catalog.
        // Bundles keyed by simple class name. Il2CppInterop wrapper types
        // share Type.Name with their managed counterparts (Sprite, Texture2D,
        // ...), so a string switch keeps this resilient to whether the
        // declared field type came from a managed assembly reference or an
        // Il2Cpp wrapper assembly.
        switch (unityType.Name)
        {
            case nameof(Sprite):
                if (_bundles.ReplacementSprites.TryGetValue(bundleKey, out var sprite))
                    return sprite;
                break;
            case nameof(Texture2D):
                if (_bundles.ReplacementTextures.TryGetValue(bundleKey, out var texture))
                    return texture;
                break;
            case nameof(AudioClip):
                if (_bundles.ReplacementAudioClips.TryGetValue(bundleKey, out var audio))
                    return audio;
                break;
                // Material additions don't have a dedicated bundle dictionary
                // yet; they fall through to the game-asset registry until the
                // bundle catalog grows a Materials dictionary.
        }

        // Phase 2: game-asset registry.
        try
        {
            var il2CppType = Il2CppType.From(unityType);
            if (il2CppType == null)
                return null;

            var candidates = Resources.FindObjectsOfTypeAll(il2CppType);
            if (candidates == null)
                return null;

            foreach (var candidate in candidates)
            {
                if (candidate != null
                    && string.Equals(candidate.name, name, System.StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Il2CppType.From throws for types it can't translate (e.g. a
            // managed type with no IL2CPP counterpart). Fall through to
            // null so the applier surfaces a clean "not found" error
            // rather than an opaque marshalling exception.
        }

        return null;
    }
}
