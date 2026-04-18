using Il2CppInterop.Runtime;
using Jiangyu.Shared.Replacements;
using Jiangyu.Loader.Replacements;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Jiangyu.Loader.Bundles;

namespace Jiangyu.Loader.Runtime;

internal class ReplacementCoordinator
{
    private readonly List<UnityEngine.Object> _pinned = new();
    private readonly BundleReplacementCatalog _catalog;
    private readonly MaterialReplacementService _materialReplacements;
    private readonly MeshPreparationService _meshPreparation;
    private readonly DirectMeshReplacementApplier _directReplacements;
    private readonly DrivenPrefabReplacementManager _drivenReplacements;

    public ReplacementCoordinator()
    {
        _catalog = new BundleReplacementCatalog(_pinned);
        _materialReplacements = new MaterialReplacementService(_catalog.ReplacementTextures, _pinned);
        _meshPreparation = new MeshPreparationService(_pinned);
        _directReplacements = new DirectMeshReplacementApplier(_materialReplacements, _meshPreparation);
        _drivenReplacements = new DrivenPrefabReplacementManager(_pinned);
    }

    public BundleLoadSummary LoadBundles(string modsDir, MelonLogger.Instance log)
        => _catalog.LoadBundles(modsDir, log);

    public void ApplyReplacements(MelonLogger.Instance log)
    {
        if (_catalog.Meshes.Count == 0 &&
            _catalog.Prefabs.Count == 0 &&
            _catalog.ReplacementTextures.Count == 0 &&
            _catalog.ReplacementSprites.Count == 0 &&
            _catalog.ReplacementAudioClips.Count == 0)
            return;

        var visualReplacements = 0;
        var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);

        foreach (var obj in skinnedRenderers)
        {
            var smr = obj.Cast<SkinnedMeshRenderer>();
            if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                continue;

            // JIANGYU-CONTRACT: Runtime mesh replacement currently resolves live targets by
            // SkinnedMeshRenderer.sharedMesh.name only. This is valid for the current proven
            // replacement path where target mesh names are unique for the selected model target.
            // Compile-time convention-first replacement must reject targets whose expected mesh
            // names are globally duplicated until Jiangyu has a stronger runtime asset identity.
            //
            // JIANGYU-CONTRACT: If Jiangyu later adds a more specific prefab-aware runtime
            // identity, the current shared-mesh behaviour must remain the fallback so one
            // replacement can still intentionally apply across multiple prefab variants that
            // share the same live mesh names. Per-variant replacement should be opt-in and win
            // only when explicitly defined.
            if (_catalog.Prefabs.TryGetValue(smr.sharedMesh.name, out var prefabReplacement))
            {
                var entityRoot = FindEntityRoot(smr);
                if (entityRoot != null &&
                    !_drivenReplacements.HasReplacementFor(entityRoot) &&
                    _drivenReplacements.TryCreate(log, entityRoot, smr, prefabReplacement))
                {
                    visualReplacements++;
                }

                continue;
            }

            if (TryGetReplacementMesh(smr.sharedMesh.name, out var resolvedMeshName, out var meshReplacement))
            {
                if (!string.Equals(resolvedMeshName, smr.sharedMesh.name, StringComparison.Ordinal))
                {
                    log.Msg($"  LOD fallback: {smr.sharedMesh.name} -> {resolvedMeshName}");
                }

                if (_directReplacements.Apply(log, smr, meshReplacement))
                {
                    visualReplacements++;
                }

                continue;
            }

            if (_materialReplacements.HasReplacementTextures &&
                smr.sharedMaterials != null &&
                smr.sharedMaterials.Length > 0 &&
                _materialReplacements.HasDirectTextureReplacementTargets(smr.sharedMaterials))
            {
                // JIANGYU-CONTRACT: Direct texture replacement currently resolves live targets by the
                // existing Material texture object's name on known texture properties. This is valid for
                // the current proven Texture2D replacement path when the target texture name is unique at
                // runtime. Compile-time convention-first replacement must reject ambiguous Texture2D
                // targets until Jiangyu has a stronger live texture identity.
                smr.sharedMaterials = _materialReplacements.GetOrCreateDirectTextureReplacementMaterials(smr.sharedMaterials);
                visualReplacements++;
            }
        }

        if (_catalog.ReplacementTextures.Count > 0)
        {
            var renderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>(), true);
            foreach (var obj in renderers)
            {
                var renderer = obj.Cast<Renderer>();
                if (renderer is SkinnedMeshRenderer || !IsLiveSceneObject(renderer?.gameObject))
                    continue;

                if (renderer.sharedMaterials == null ||
                    renderer.sharedMaterials.Length == 0 ||
                    !_materialReplacements.HasDirectTextureReplacementTargets(renderer.sharedMaterials))
                {
                    continue;
                }

                renderer.sharedMaterials = _materialReplacements.GetOrCreateDirectTextureReplacementMaterials(renderer.sharedMaterials);
                visualReplacements++;
            }
        }

        var spriteReplacements = 0;
        if (_catalog.ReplacementSprites.Count > 0)
        {
            var spriteRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>(), true);
            foreach (var obj in spriteRenderers)
            {
                var spriteRenderer = obj.Cast<SpriteRenderer>();
                if (!IsLiveSceneObject(spriteRenderer?.gameObject))
                    continue;

                var sprite = spriteRenderer.sprite;
                if (sprite == null || !_catalog.ReplacementSprites.TryGetValue(sprite.name, out var replacementSprite))
                    continue;

                // JIANGYU-CONTRACT: Scoped direct sprite sweep on SpriteRenderer.sprite.name.
                // Not a general Sprite replacement contract. In-game smoke test (2026-04-18,
                // icon_hitpoints) found zero matches even with a unique target name, because
                // live UI sprites are routed through sprite atlases and/or ScriptableObject
                // references that this sweep does not reach. See
                // docs/research/investigations/2026-04-18-sprite-audio-runtime-routing.md.
                spriteRenderer.sprite = replacementSprite;
                spriteReplacements++;
            }

            var images = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Image>(), true);
            foreach (var obj in images)
            {
                var image = obj.Cast<Image>();
                if (!IsLiveSceneObject(image?.gameObject))
                    continue;

                var sprite = image.sprite;
                if (sprite == null || !_catalog.ReplacementSprites.TryGetValue(sprite.name, out var replacementSprite))
                    continue;

                // JIANGYU-CONTRACT: Scoped direct sprite sweep on UnityEngine.UI.Image.sprite.name.
                // Not a general Sprite replacement contract. See the SpriteRenderer block above and
                // the 2026-04-18 investigation note for why UI sprites often bypass this surface.
                image.sprite = replacementSprite;
                spriteReplacements++;
            }
        }

        var audioReplacements = 0;
        if (_catalog.ReplacementAudioClips.Count > 0)
        {
            var audioSources = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<AudioSource>(), true);
            foreach (var obj in audioSources)
            {
                var audioSource = obj.Cast<AudioSource>();
                if (!IsLiveSceneObject(audioSource?.gameObject))
                    continue;

                var clip = audioSource.clip;
                if (clip == null || !_catalog.ReplacementAudioClips.TryGetValue(clip.name, out var replacementClip))
                    continue;

                // JIANGYU-CONTRACT: Scoped direct audio sweep on AudioSource.clip.name.
                // Not a general AudioClip replacement contract. In-game smoke test (2026-04-18,
                // button_click_01) swapped one AudioSource.clip but the UI click was still
                // audibly unchanged, because MENACE routes UI SFX through an audio manager
                // that caches AudioClip references outside any scene-resident AudioSource.clip
                // field. PlayOneShot uses its argument clip, ignoring AudioSource.clip. See
                // docs/research/investigations/2026-04-18-sprite-audio-runtime-routing.md.
                audioSource.clip = replacementClip;
                audioReplacements++;
            }
        }

        if (visualReplacements > 0 || spriteReplacements > 0 || audioReplacements > 0)
            log.Msg($"Applied {visualReplacements} visual replacement(s), {spriteReplacements} sprite replacement(s), and {audioReplacements} audio replacement(s).");
    }

    public bool HasReplacementTargets()
    {
        if (_catalog.Meshes.Count == 0 &&
            _catalog.Prefabs.Count == 0 &&
            _catalog.ReplacementTextures.Count == 0 &&
            _catalog.ReplacementSprites.Count == 0 &&
            _catalog.ReplacementAudioClips.Count == 0)
            return false;

        if (_catalog.Meshes.Count > 0 || _catalog.Prefabs.Count > 0 || _catalog.ReplacementTextures.Count > 0)
        {
            var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            foreach (var obj in skinnedRenderers)
            {
                var smr = obj.Cast<SkinnedMeshRenderer>();
                if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                    continue;

                if (_catalog.Prefabs.ContainsKey(smr.sharedMesh.name) || TryGetReplacementMesh(smr.sharedMesh.name, out _, out _))
                    return true;

                if (_catalog.ReplacementTextures.Count > 0 &&
                    smr.sharedMaterials != null &&
                    smr.sharedMaterials.Length > 0 &&
                    _materialReplacements.HasDirectTextureReplacementTargets(smr.sharedMaterials))
                {
                    return true;
                }
            }

            if (_catalog.ReplacementTextures.Count > 0)
            {
                var renderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>(), true);
                foreach (var obj in renderers)
                {
                    var renderer = obj.Cast<Renderer>();
                    if (renderer is SkinnedMeshRenderer || !IsLiveSceneObject(renderer?.gameObject))
                        continue;

                    if (renderer.sharedMaterials != null &&
                        renderer.sharedMaterials.Length > 0 &&
                        _materialReplacements.HasDirectTextureReplacementTargets(renderer.sharedMaterials))
                    {
                        return true;
                    }
                }
            }
        }

        if (_catalog.ReplacementSprites.Count > 0)
        {
            var spriteRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>(), true);
            foreach (var obj in spriteRenderers)
            {
                var spriteRenderer = obj.Cast<SpriteRenderer>();
                if (!IsLiveSceneObject(spriteRenderer?.gameObject))
                    continue;

                var sprite = spriteRenderer.sprite;
                if (sprite != null && _catalog.ReplacementSprites.ContainsKey(sprite.name))
                    return true;
            }

            var images = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Image>(), true);
            foreach (var obj in images)
            {
                var image = obj.Cast<Image>();
                if (!IsLiveSceneObject(image?.gameObject))
                    continue;

                var sprite = image.sprite;
                if (sprite != null && _catalog.ReplacementSprites.ContainsKey(sprite.name))
                    return true;
            }
        }

        if (_catalog.ReplacementAudioClips.Count > 0)
        {
            var audioSources = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<AudioSource>(), true);
            foreach (var obj in audioSources)
            {
                var audioSource = obj.Cast<AudioSource>();
                if (!IsLiveSceneObject(audioSource?.gameObject))
                    continue;

                var clip = audioSource.clip;
                if (clip != null && _catalog.ReplacementAudioClips.ContainsKey(clip.name))
                    return true;
            }
        }

        return false;
    }

    public void UpdateDrivenReplacements(MelonLogger.Instance log)
        => _drivenReplacements.Update(log);

    private static bool IsLiveSceneRenderer(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.sharedMesh == null)
            return false;

        if (smr.hideFlags != HideFlags.None)
            return false;

        return IsLiveSceneObject(smr.gameObject);
    }

    private static bool IsLiveSceneObject(GameObject gameObject)
    {
        if (gameObject == null || gameObject.hideFlags != HideFlags.None)
            return false;

        var scene = gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private static bool IsAlreadyProcessedMesh(Mesh mesh)
        => mesh != null && mesh.name.EndsWith(" [jiangyu]", StringComparison.Ordinal);

    private bool TryGetReplacementMesh(string requestedMeshName, out string resolvedMeshName, out ReplacementMesh replacement)
    {
        resolvedMeshName = string.Empty;
        if (_catalog.Meshes.TryGetValue(requestedMeshName, out replacement))
        {
            resolvedMeshName = requestedMeshName;
            return true;
        }

        var fallbackName = LodReplacementResolver.FindNearestAvailableTarget(_catalog.Meshes.Keys, requestedMeshName);
        if (fallbackName is null || !_catalog.Meshes.TryGetValue(fallbackName, out replacement))
            return false;

        resolvedMeshName = fallbackName;
        return true;
    }

    private static GameObject FindEntityRoot(SkinnedMeshRenderer smr)
    {
        if (smr == null)
            return null;

        Transform bestNamedCandidate = null;
        var current = smr.transform;
        while (current != null)
        {
            if (current.GetComponent<Animator>() != null)
                return current.gameObject;

            var name = current.name;
            if (!string.IsNullOrEmpty(name) &&
                !name.Contains("_LOD", StringComparison.OrdinalIgnoreCase) &&
                (name.StartsWith("rmc_", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("soldier", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("enemy", StringComparison.OrdinalIgnoreCase)))
            {
                bestNamedCandidate = current;
            }

            current = current.parent;
        }

        if (bestNamedCandidate != null)
            return bestNamedCandidate.gameObject;

        return smr.rootBone?.root?.gameObject ?? smr.transform.root.gameObject;
    }
}
