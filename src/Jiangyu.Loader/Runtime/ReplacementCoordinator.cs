using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Jiangyu.Loader.Bundles;
using Jiangyu.Loader.Replacements;

namespace Jiangyu.Loader.Runtime;

public class ReplacementCoordinator
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
        if (_catalog.Meshes.Count == 0 && _catalog.Prefabs.Count == 0)
            return;

        var replaced = 0;
        var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);

        foreach (var obj in skinnedRenderers)
        {
            var smr = obj.Cast<SkinnedMeshRenderer>();
            if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                continue;

            if (_catalog.Prefabs.TryGetValue(smr.sharedMesh.name, out var prefabReplacement))
            {
                var entityRoot = FindEntityRoot(smr);
                if (entityRoot != null &&
                    !_drivenReplacements.HasReplacementFor(entityRoot) &&
                    _drivenReplacements.TryCreate(log, entityRoot, smr, prefabReplacement))
                {
                    replaced++;
                }

                continue;
            }

            if (_catalog.Meshes.TryGetValue(smr.sharedMesh.name, out var meshReplacement) &&
                _directReplacements.Apply(log, smr, meshReplacement))
            {
                replaced++;
            }
        }

        if (replaced > 0)
            log.Msg($"Applied {replaced} mesh replacement(s).");
    }

    public bool HasReplacementTargets()
    {
        if (_catalog.Meshes.Count == 0 && _catalog.Prefabs.Count == 0)
            return false;

        var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        foreach (var obj in skinnedRenderers)
        {
            var smr = obj.Cast<SkinnedMeshRenderer>();
            if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                continue;

            if (_catalog.Prefabs.ContainsKey(smr.sharedMesh.name) || _catalog.Meshes.ContainsKey(smr.sharedMesh.name))
                return true;
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
        if (smr.gameObject.hideFlags != HideFlags.None)
            return false;

        var scene = smr.gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private static bool IsAlreadyProcessedMesh(Mesh mesh)
        => mesh != null && mesh.name.EndsWith(" [jiangyu]", StringComparison.Ordinal);

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
