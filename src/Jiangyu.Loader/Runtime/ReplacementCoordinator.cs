using Il2CppInterop.Runtime;
using Jiangyu.Loader.Bundles;
using Jiangyu.Loader.Replacements;
using Jiangyu.Shared.Replacements;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Runtime;

internal class ReplacementCoordinator
{
    private readonly List<UnityEngine.Object> _pinned = new();
    private readonly BundleReplacementCatalog _catalog;
    private readonly MaterialReplacementService _materialReplacements;
    private readonly TextureMutationService _textureMutation;
    private readonly MeshPreparationService _meshPreparation;
    private readonly DirectMeshReplacementApplier _directReplacements;
    private readonly DrivenPrefabReplacementManager _drivenReplacements;
    // Tracks SMRs we've already processed in this session. The mesh-name-based
    // IsAlreadyProcessedMesh check isn't sufficient because the "direct use"
    // swap path assigns the bundle's mesh verbatim (no " [jiangyu]" suffix
    // added), so subsequent sweeps can't tell the SMR apart from an un-swapped
    // one. Without this, the continuous spawn monitor re-swaps every already-
    // handled SMR every tick, which scales badly with unit count.
    private readonly HashSet<int> _processedSmrInstanceIds = new();

    public ReplacementCoordinator()
    {
        _catalog = new BundleReplacementCatalog(_pinned);
        _materialReplacements = new MaterialReplacementService(_catalog.ReplacementTextures, _pinned);
        _textureMutation = new TextureMutationService(_catalog.ReplacementTextures);
        _meshPreparation = new MeshPreparationService(_pinned);
        _directReplacements = new DirectMeshReplacementApplier(_materialReplacements, _meshPreparation);
        _drivenReplacements = new DrivenPrefabReplacementManager(_pinned);
    }

    public BundleLoadSummary LoadBundles(string modsDir, MelonLogger.Instance log)
        => _catalog.LoadBundles(modsDir, log);

    public void InstallHarmonyPatches(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        AudioReplacementPatch.Install(harmony, _catalog.ReplacementAudioClips, log);
    }

    public bool HasMeshOrPrefabReplacements =>
        _catalog.Meshes.Count > 0 || _catalog.Prefabs.Count > 0;

    public void OnSceneUnloaded()
    {
        // SMR instance IDs are scene-scoped; SMRs destroyed with the scene take
        // their IDs with them. Clearing avoids slowly accumulating dead entries
        // across scenes and avoids (theoretical) ID-recycling false-negatives
        // on later-scene SMRs that happen to reuse a destroyed ID.
        _processedSmrInstanceIds.Clear();
    }

    public void ApplyReplacements(MelonLogger.Instance log, bool includeTextures = true)
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
            if (smr == null)
                continue;

            var smrInstanceId = smr.GetInstanceID();
            if (_processedSmrInstanceIds.Contains(smrInstanceId))
                continue;

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
                    _processedSmrInstanceIds.Add(smrInstanceId);
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
                    _processedSmrInstanceIds.Add(smrInstanceId);
                }
            }
        }

        var textureMutations = includeTextures ? _textureMutation.ApplyPending(log) : 0;

        if (visualReplacements > 0 || textureMutations > 0)
            log.Msg($"Applied {visualReplacements} visual replacement(s) and {textureMutations} texture mutation(s).");
    }

    public bool HasReplacementTargets()
    {
        if (_catalog.Meshes.Count == 0 &&
            _catalog.Prefabs.Count == 0 &&
            _catalog.ReplacementTextures.Count == 0 &&
            _catalog.ReplacementSprites.Count == 0 &&
            _catalog.ReplacementAudioClips.Count == 0)
            return false;

        if (_catalog.Meshes.Count > 0 || _catalog.Prefabs.Count > 0)
        {
            var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            foreach (var obj in skinnedRenderers)
            {
                var smr = obj.Cast<SkinnedMeshRenderer>();
                if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                    continue;

                if (_catalog.Prefabs.ContainsKey(smr.sharedMesh.name) || TryGetReplacementMesh(smr.sharedMesh.name, out _, out _))
                    return true;
            }
        }

        if (_textureMutation.HasPendingTargets())
            return true;

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
