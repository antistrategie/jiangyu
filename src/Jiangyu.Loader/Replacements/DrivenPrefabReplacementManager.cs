using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class DrivenPrefabReplacementManager
{
    private static readonly Bounds ForcedReplacementRendererBounds = new(Vector3.zero, Vector3.one * 10f);

    private readonly Dictionary<int, DrivenReplacement> _drivenReplacements = new();
    private readonly List<UnityEngine.Object> _pinned;

    private sealed class DrivenReplacement
    {
        public GameObject EntityRoot;
        public GameObject ReplacementRoot;
        public Transform SourceSkeletonRoot;
        public Transform ReplacementSkeletonRoot;
        public TransformPair[] BonePairs;
        public bool LoggedInitialSync;
    }

    private sealed class TransformPair
    {
        public string Name;
        public Transform Source;
        public Transform Target;
    }

    public DrivenPrefabReplacementManager(List<UnityEngine.Object> pinned)
    {
        _pinned = pinned;
    }

    public bool HasReplacementFor(GameObject entityRoot)
        => entityRoot != null && _drivenReplacements.ContainsKey(entityRoot.GetInstanceID());

    public bool TryCreate(MelonLogger.Instance log, GameObject entityRoot, SkinnedMeshRenderer sourceSmr, ReplacementPrefab replacementPrefab)
    {
        if (entityRoot == null || sourceSmr == null || replacementPrefab?.Template == null)
            return false;

        var sourceSkeletonRoot = sourceSmr.rootBone?.root ?? sourceSmr.transform.root;
        if (sourceSkeletonRoot == null)
            return false;

        var hiddenSkinnedRenderers = entityRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var hiddenRenderers = new Renderer[hiddenSkinnedRenderers.Length];
        for (int i = 0; i < hiddenSkinnedRenderers.Length; i++)
            hiddenRenderers[i] = hiddenSkinnedRenderers[i];

        var replacementRoot = UnityEngine.Object.Instantiate(replacementPrefab.Template);
        replacementRoot.name = $"{replacementPrefab.PrefabName} [jiangyu-driven]";
        replacementRoot.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _pinned.Add(replacementRoot);
        replacementRoot.SetActive(true);

        replacementRoot.transform.SetParent(entityRoot.transform, false);
        replacementRoot.transform.localPosition = Vector3.zero;
        replacementRoot.transform.localRotation = Quaternion.identity;
        replacementRoot.transform.localScale = Vector3.one;

        ConfigureReplacementRenderers(replacementRoot, sourceSmr, log);

        var replacementAnimator = replacementRoot.GetComponent<Animator>();
        if (replacementAnimator != null)
            replacementAnimator.enabled = false;

        var replacementSkeletonRoot = FindSkeletonRoot(replacementRoot);
        var sourceMap = BuildTransformMap(sourceSkeletonRoot);
        var replacementMap = BuildTransformMap(replacementSkeletonRoot);

        var pairs = new List<TransformPair>();
        var misses = new List<string>();
        foreach (var (name, targetTransform) in replacementMap)
        {
            if (sourceMap.TryGetValue(name, out var sourceTransform))
            {
                pairs.Add(new TransformPair
                {
                    Name = name,
                    Source = sourceTransform,
                    Target = targetTransform,
                });
            }
            else if (misses.Count < 8)
            {
                misses.Add(name);
            }
        }

        if (pairs.Count == 0)
        {
            UnityEngine.Object.Destroy(replacementRoot);
            log.Warning($"  [DRIVE] {entityRoot.name}: no matching bones for prefab '{replacementPrefab.PrefabName}'");
            return false;
        }

        DisableOriginalRenderers(hiddenRenderers);

        var driven = new DrivenReplacement
        {
            EntityRoot = entityRoot,
            ReplacementRoot = replacementRoot,
            SourceSkeletonRoot = sourceSkeletonRoot,
            ReplacementSkeletonRoot = replacementSkeletonRoot,
            BonePairs = pairs.ToArray(),
        };

        _drivenReplacements[entityRoot.GetInstanceID()] = driven;

        log.Msg($"  [DRIVE] attached prefab '{replacementPrefab.PrefabName}' to '{entityRoot.name}' hiddenRenderers={hiddenRenderers.Length} mappedBones={pairs.Count}/{replacementMap.Count}");
        log.Msg($"  [DRIVE] source skeleton root='{sourceSkeletonRoot.name}' pos={FormatVector3(sourceSkeletonRoot.position)} scale={FormatVector3(sourceSkeletonRoot.lossyScale)}");
        log.Msg($"  [DRIVE] replacement skeleton root='{replacementSkeletonRoot.name}' pos={FormatVector3(replacementSkeletonRoot.position)} scale={FormatVector3(replacementSkeletonRoot.lossyScale)}");
        if (misses.Count > 0)
            log.Warning($"  [DRIVE] first missing bones: {string.Join(", ", misses)}");

        return true;
    }

    public void Update(MelonLogger.Instance log)
    {
        if (_drivenReplacements.Count == 0)
            return;

        var staleIds = new List<int>();
        foreach (var (id, driven) in _drivenReplacements)
        {
            if (!IsDrivenReplacementAlive(driven))
            {
                staleIds.Add(id);
                continue;
            }

            foreach (var pair in driven.BonePairs)
            {
                if (pair.Source == null || pair.Target == null)
                    continue;

                pair.Target.position = pair.Source.position;
                pair.Target.rotation = pair.Source.rotation;
                pair.Target.localScale = pair.Source.localScale;
            }

            if (!driven.LoggedInitialSync)
            {
                driven.LoggedInitialSync = true;
                const int sampleCount = 5;
                foreach (var sample in driven.BonePairs.Take(sampleCount))
                {
                    if (sample.Source == null || sample.Target == null)
                        continue;

                    log.Msg($"  [DRIVE] sample {sample.Name} sourcePos={FormatVector3(sample.Source.position)} targetPos={FormatVector3(sample.Target.position)}");
                }
            }
        }

        foreach (var staleId in staleIds)
            _drivenReplacements.Remove(staleId);
    }

    private static string FormatVector3(Vector3 v)
        => $"({v.x:F4}, {v.y:F4}, {v.z:F4})";

    private static void CollectBonesRecursive(Transform parent, Dictionary<string, Transform> map)
    {
        if (parent == null)
            return;

        map.TryAdd(parent.name, parent);
        for (int i = 0; i < parent.childCount; i++)
            CollectBonesRecursive(parent.GetChild(i), map);
    }

    private static Dictionary<string, Transform> BuildTransformMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
        CollectBonesRecursive(root, map);
        return map;
    }

    private static Transform FindSkeletonRoot(GameObject rootObject)
    {
        if (rootObject == null)
            return null;

        var smr = rootObject.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (smr?.rootBone != null)
            return smr.rootBone.root;

        return rootObject.transform;
    }

    private static void DisableOriginalRenderers(Renderer[] renderers)
    {
        foreach (var renderer in renderers)
        {
            if (renderer != null)
                renderer.enabled = false;
        }
    }

    private static void ConfigureReplacementRenderers(GameObject replacementRoot, SkinnedMeshRenderer sourceSmr, MelonLogger.Instance log)
    {
        if (replacementRoot == null)
            return;

        var lodGroup = replacementRoot.GetComponentInChildren<LODGroup>(true);
        log.Msg($"  [DRIVE] replacement has LODGroup={lodGroup != null}");

        var replacementSmrs = replacementRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        log.Msg($"  [DRIVE] replacement skinned renderer count={replacementSmrs.Length}");
        foreach (var smr in replacementSmrs)
        {
            if (smr == null)
                continue;

            smr.enabled = true;
            smr.updateWhenOffscreen = true;
            smr.localBounds = ForcedReplacementRendererBounds;

            if (sourceSmr?.sharedMaterials != null && sourceSmr.sharedMaterials.Length > 0)
                smr.sharedMaterials = sourceSmr.sharedMaterials;

            log.Msg($"  [DRIVE] replacement SMR '{smr.name}' mesh='{smr.sharedMesh?.name ?? "<null>"}' mats={smr.sharedMaterials?.Length ?? 0} rootBone='{smr.rootBone?.name ?? "<null>"}' enabled={smr.enabled} active={smr.gameObject.activeInHierarchy} boundsCenter={FormatVector3(smr.localBounds.center)} boundsSize={FormatVector3(smr.localBounds.size)}");
        }

        var renderers = replacementRoot.GetComponentsInChildren<Renderer>(true);
        log.Msg($"  [DRIVE] replacement renderer count={renderers.Length}");
        foreach (var renderer in renderers)
        {
            if (renderer == null)
                continue;

            renderer.enabled = true;
            log.Msg($"  [DRIVE] replacement renderer '{renderer.name}' type={renderer.GetType().Name} enabled={renderer.enabled} active={renderer.gameObject.activeInHierarchy}");
        }
    }

    private static bool IsDrivenReplacementAlive(DrivenReplacement driven)
    {
        if (driven == null)
            return false;

        return driven.EntityRoot != null &&
               driven.ReplacementRoot != null &&
               driven.SourceSkeletonRoot != null &&
               driven.ReplacementSkeletonRoot != null;
    }
}
