using Il2CppInterop.Runtime;
using Jiangyu.Loader.Bundles;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

// Rebinds the mesh on a vanilla source prefab's SkinnedMeshRenderer rather
// than on each spawned instance. SkinnedMeshRenderer.sharedMesh is a shared
// reference across Instantiate'd copies, so every future spawn of the prefab
// inherits the rebound mesh by reference. This is the only path that
// survives the modular-vehicle visual rebuild cycle, where the chassis is
// destroyed and respawned faster than a per-instance scan can keep up with.
internal sealed class PrefabMeshRebindApplier
{
    private readonly BundleReplacementCatalog _catalog;
    private readonly DirectMeshReplacementApplier _directApplier;

    public PrefabMeshRebindApplier(
        BundleReplacementCatalog catalog,
        DirectMeshReplacementApplier directApplier)
    {
        _catalog = catalog;
        _directApplier = directApplier;
    }

    public int Apply(MelonLogger.Instance log)
    {
        var pending = CollectPending();
        if (pending.Count == 0)
            return 0;

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in pending)
            wanted.Add(entry.TargetEntityName);

        var prefabsByName = BuildPrefabIndex(wanted);
        if (prefabsByName.Count == 0)
            return 0;

        var applied = 0;
        foreach (var replacement in pending)
        {
            if (!prefabsByName.TryGetValue(replacement.TargetEntityName, out var prefabRoot))
                continue;

            var smr = FindRendererAtPath(prefabRoot.transform, replacement.TargetRendererPath);
            if (smr == null || smr.sharedMesh == null)
                continue;

            if (_directApplier.Apply(log, smr, replacement))
            {
                replacement.AppliedToPrefab = true;
                applied++;
            }
        }

        if (applied > 0)
            log.Msg($"Applied {applied} prefab-time mesh rebind(s).");

        return applied;
    }

    private List<ReplacementMesh> CollectPending()
    {
        var pending = new List<ReplacementMesh>();
        foreach (var entry in _catalog.Meshes.Values)
        {
            if (entry.AppliedToPrefab)
                continue;
            if (string.IsNullOrEmpty(entry.TargetEntityName))
                continue;
            if (string.IsNullOrEmpty(entry.TargetRendererPath))
                continue;
            pending.Add(entry);
        }
        return pending;
    }

    // Resources.FindObjectsOfTypeAll is what surfaces prefab assets that
    // Object.FindObjectsOfType misses. Scene clones are skipped: rebinding a
    // clone's SMR works only for that one instance, which the per-instance
    // sweep already handles. The point of this pass is reference-sharing via
    // the prefab.
    private static Dictionary<string, GameObject> BuildPrefabIndex(HashSet<string> wanted)
    {
        var dict = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
        if (all == null)
            return dict;

        foreach (var obj in all)
        {
            var go = obj.Cast<GameObject>();
            if (!wanted.Contains(go.name))
                continue;
            if (!IsPrefabAsset(go))
                continue;
            dict[go.name] = go;
        }
        return dict;
    }

    private static bool IsPrefabAsset(GameObject go)
    {
        var scene = go.scene;
        return !scene.IsValid() || !scene.isLoaded;
    }

    private static SkinnedMeshRenderer FindRendererAtPath(Transform root, string path)
    {
        var node = root.Find(path);
        return node != null ? node.GetComponent<SkinnedMeshRenderer>() : null;
    }
}
