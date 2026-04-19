using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class DirectMeshReplacementApplier
{
    private readonly MaterialReplacementService _materialReplacements;
    private readonly MeshPreparationService _meshPreparation;

    public DirectMeshReplacementApplier(MaterialReplacementService materialReplacements, MeshPreparationService meshPreparation)
    {
        _materialReplacements = materialReplacements;
        _meshPreparation = meshPreparation;
    }

    public bool Apply(MelonLogger.Instance log, SkinnedMeshRenderer smr, ReplacementMesh replacement)
    {
        try
        {
            var gameBones = ToManagedBones(smr.bones);
            var gameBoneMap = BuildBoneMap(smr, gameBones);

            var targetBoneNames = GetBoneNames(gameBones);
            var replacementBoneNames = replacement.BoneNames;
            if (replacementBoneNames == null || replacementBoneNames.Length == 0)
                replacementBoneNames = targetBoneNames;

            replacementBoneNames ??= Array.Empty<string>();

            var stripLeadingRoot = ShouldStripLeadingRoot(targetBoneNames, replacementBoneNames);
            var effectiveBoneNames = stripLeadingRoot
                ? replacementBoneNames[1..]
                : replacementBoneNames;

            if (effectiveBoneNames.Length == 0)
            {
                log.Warning($"  [DIAG] skipping swap for '{replacement.Mesh?.name ?? "<null>"}': replacement has no effective bone names.");
                return false;
            }

            var newBones = new Transform[effectiveBoneNames.Length];
            var canReuseGameBoneOrder = CanReuseGameBoneOrder(targetBoneNames, effectiveBoneNames);
            if (canReuseGameBoneOrder)
            {
                Array.Copy(gameBones, newBones, effectiveBoneNames.Length);
            }
            else
            {
                for (int i = 0; i < effectiveBoneNames.Length; i++)
                    gameBoneMap.TryGetValue(effectiveBoneNames[i], out newBones[i]);
            }

            var missingBoneNames = Array.Empty<string>();
            if (!canReuseGameBoneOrder)
            {
                missingBoneNames = effectiveBoneNames
                    .Where((name, idx) => string.IsNullOrWhiteSpace(name) || newBones[idx] == null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                if (missingBoneNames.Length > 0)
                {
                    log.Warning(
                        $"  [DIAG] replacement mesh '{replacement.Mesh?.name ?? "<null>"}' is missing {missingBoneNames.Length} mapped live bone(s): {FormatNameListPreview(missingBoneNames, 12)}");
                }
            }

            if (missingBoneNames.Length == effectiveBoneNames.Length)
            {
                log.Warning($"  [DIAG] skipping swap for '{replacement.Mesh?.name ?? "<null>"}': no live bones could be mapped.");
                return false;
            }

            var newRootBone = canReuseGameBoneOrder
                ? smr.rootBone
                : ResolveRootBone(smr.rootBone, replacementBoneNames, stripLeadingRoot, gameBoneMap);
            var prepared = _meshPreparation.GetOrPrepare(log, smr, replacement, targetBoneNames, effectiveBoneNames, gameBones, newBones);
            var safeBounds = MergeBounds(smr.localBounds, prepared.Bounds);

            smr.sharedMesh = prepared.Mesh;
            smr.bones = new Il2CppReferenceArray<Transform>(newBones);
            smr.rootBone = newRootBone;
            smr.localBounds = safeBounds;
            smr.updateWhenOffscreen = prepared.UpdateWhenOffscreen;

            if (smr.sharedMaterials != null &&
                smr.sharedMaterials.Length > 0 &&
                replacement.MaterialBindings != null &&
                replacement.MaterialBindings.Length > 0 &&
                _materialReplacements.HasReplacementTextures)
            {
                _materialReplacements.ApplyBindings(log, smr.sharedMaterials, replacement.MaterialBindings);
            }

            log.Msg($"  Swapped: {smr.sharedMesh.name} (readable={smr.sharedMesh.isReadable}, rootBone={smr.rootBone?.name ?? "<null>"}, boundsCenter={FormatVector3(safeBounds.center)}, boundsSize={FormatVector3(safeBounds.size)}, updateWhenOffscreen={smr.updateWhenOffscreen})");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"  [DIAG] direct mesh swap failed for '{replacement.Mesh?.name ?? "<null>"}' on renderer '{smr?.name ?? "<null>"}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static Bounds MergeBounds(Bounds originalBounds, Bounds replacementBounds)
    {
        var merged = originalBounds;
        merged.Encapsulate(replacementBounds.min);
        merged.Encapsulate(replacementBounds.max);
        return merged;
    }

    private static Dictionary<string, Transform> BuildBoneMap(SkinnedMeshRenderer smr, Transform[] gameBones)
    {
        var gameBoneMap = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (var bone in gameBones)
        {
            if (bone != null)
                gameBoneMap[bone.name] = bone;
        }

        if (smr.rootBone != null)
            CollectBonesRecursive(smr.rootBone.root, gameBoneMap);

        return gameBoneMap;
    }

    private static Transform ResolveRootBone(
        Transform currentRootBone,
        string[] replacementBoneNames,
        bool stripLeadingRoot,
        Dictionary<string, Transform> gameBoneMap)
    {
        var newRootBone = currentRootBone;
        if (!stripLeadingRoot &&
            replacementBoneNames.Length > 0 &&
            !string.IsNullOrEmpty(replacementBoneNames[0]) &&
            gameBoneMap.TryGetValue(replacementBoneNames[0], out var mappedRoot))
        {
            newRootBone = mappedRoot;
        }

        return newRootBone;
    }

    private static string[] GetBoneNames(Transform[] bones)
    {
        var names = new string[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            names[i] = bones[i]?.name ?? string.Empty;
        return names;
    }

    private static string FormatVector3(Vector3 v)
        => $"({v.x:F4}, {v.y:F4}, {v.z:F4})";

    private static bool CanReuseGameBoneOrder(string[] targetBoneNames, string[] effectiveBoneNames)
    {
        if (targetBoneNames == null || effectiveBoneNames == null)
            return false;

        if (targetBoneNames.Length != effectiveBoneNames.Length)
            return false;

        for (int i = 0; i < targetBoneNames.Length; i++)
        {
            if (!string.Equals(targetBoneNames[i], effectiveBoneNames[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string FormatNameListPreview(IEnumerable<string> names, int maxItems)
    {
        var ordered = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length <= maxItems)
            return string.Join(", ", ordered);

        var preview = ordered.Take(maxItems);
        return $"{string.Join(", ", preview)} (+{ordered.Length - maxItems} more)";
    }

    private static bool ShouldStripLeadingRoot(string[] gameBoneNames, string[] replacementBoneNames)
    {
        // Structural check: the replacement has exactly one extra bone and its tail
        // (index 1..N+1) matches the game's full bone list in order. This catches the
        // case where a third-party authoring tool wraps the skeleton in an additional
        // parent bone. The wrapper's name is not checked — the structural superset
        // match is sufficient, since any mismatch past slot 0 causes the pairwise
        // comparison to fail.
        if (gameBoneNames == null || replacementBoneNames == null)
            return false;

        if (replacementBoneNames.Length != gameBoneNames.Length + 1)
            return false;

        for (int i = 0; i < gameBoneNames.Length; i++)
        {
            if (!string.Equals(gameBoneNames[i], replacementBoneNames[i + 1], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static void CollectBonesRecursive(Transform parent, Dictionary<string, Transform> map)
    {
        if (parent == null)
            return;

        map.TryAdd(parent.name, parent);
        for (int i = 0; i < parent.childCount; i++)
            CollectBonesRecursive(parent.GetChild(i), map);
    }

    private static Transform[] ToManagedBones(Il2CppReferenceArray<Transform> bones)
    {
        if (bones == null || bones.Length == 0)
            return Array.Empty<Transform>();

        var result = new Transform[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            result[i] = bones[i];

        return result;
    }

}
