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
        var gameBones = smr.bones;
        var gameBoneMap = BuildBoneMap(smr, gameBones);

        var targetBoneNames = GetBoneNames(gameBones);
        var replacementBoneNames = replacement.BoneNames;
        if ((replacementBoneNames == null || replacementBoneNames.Length == 0) && gameBones.Length > 0)
            replacementBoneNames = targetBoneNames;

        var stripLeadingRoot = ShouldStripLeadingRoot(targetBoneNames, replacementBoneNames);
        var effectiveBoneNames = stripLeadingRoot
            ? replacementBoneNames[1..]
            : replacementBoneNames;

        var newBones = new Transform[effectiveBoneNames.Length];
        for (int i = 0; i < effectiveBoneNames.Length; i++)
            gameBoneMap.TryGetValue(effectiveBoneNames[i], out newBones[i]);

        var newRootBone = ResolveRootBone(smr.rootBone, replacementBoneNames, stripLeadingRoot, gameBoneMap);
        var prepared = _meshPreparation.GetOrPrepare(log, smr, replacement, targetBoneNames, effectiveBoneNames, newBones);

        smr.sharedMesh = prepared.Mesh;
        smr.bones = new Il2CppReferenceArray<Transform>(newBones);
        smr.rootBone = newRootBone;
        smr.localBounds = prepared.Bounds;
        smr.updateWhenOffscreen = prepared.UpdateWhenOffscreen;

        if (replacement.MaterialBindings != null &&
            replacement.MaterialBindings.Length > 0 &&
            smr.sharedMaterials != null &&
            smr.sharedMaterials.Length > 0 &&
            _materialReplacements.HasReplacementTextures)
        {
            smr.sharedMaterials = _materialReplacements.GetOrCreateReplacementMaterials(smr.sharedMaterials, replacement.MaterialBindings);
        }

        log.Msg($"  Swapped: {smr.sharedMesh.name} (readable={smr.sharedMesh.isReadable}, rootBone={smr.rootBone?.name ?? "<null>"}, boundsCenter={FormatVector3(prepared.Bounds.center)}, boundsSize={FormatVector3(prepared.Bounds.size)}, updateWhenOffscreen={smr.updateWhenOffscreen})");
        return true;
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

    private static bool ShouldStripLeadingRoot(string[] gameBoneNames, string[] replacementBoneNames)
    {
        if (replacementBoneNames.Length != gameBoneNames.Length + 1)
            return false;
        if (!string.Equals(replacementBoneNames[0], "Root", StringComparison.Ordinal))
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
}
