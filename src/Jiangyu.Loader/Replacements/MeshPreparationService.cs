using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class MeshPreparationService
{
    private readonly record struct PreparedMeshKey(int OriginalMeshId, int ReplacementMeshId);

    private readonly List<UnityEngine.Object> _pinned;
    private readonly Dictionary<PreparedMeshKey, PreparedMeshAssignment> _preparedAssignments = new();

    public MeshPreparationService(List<UnityEngine.Object> pinned)
    {
        _pinned = pinned;
    }

    public PreparedMeshAssignment GetOrPrepare(
        MelonLogger.Instance log,
        SkinnedMeshRenderer smr,
        ReplacementMesh replacement,
        string[] targetBoneNames,
        string[] replacementBoneNames,
        Transform[] targetBones,
        Transform[] newBones)
    {
        var originalMesh = smr.sharedMesh;
        var key = new PreparedMeshKey(
            originalMesh.GetInstanceID(),
            replacement.Mesh?.GetInstanceID() ?? 0);
        if (_preparedAssignments.TryGetValue(key, out var prepared))
            return prepared;

        prepared = new PreparedMeshAssignment
        {
            UpdateWhenOffscreen = true,
        };

        var effectiveReplacementMesh = replacement.Mesh;

        var replacementHasSkinningMetadata = replacement.BoneNames != null && replacement.BoneNames.Length > 0;
        if (!replacementHasSkinningMetadata && MeshPreparationUtilities.TargetMeshVertexCountMatches(originalMesh, effectiveReplacementMesh))
        {
            var runtimeMesh = MeshPreparationUtilities.InstantiateMeshClone(originalMesh);
            _pinned.Add(runtimeMesh);

            if (MeshPreparationUtilities.TryCopyGeometryOntoExistingMesh(log, runtimeMesh, effectiveReplacementMesh))
            {
                prepared.Mesh = runtimeMesh;
                prepared.Bounds = runtimeMesh.bounds;
                log.Msg($"  Prepared runtime mesh clone: {runtimeMesh.name} from {originalMesh.name}");
            }
        }

        if (prepared.Mesh == null)
        {
            if (MeshPreparationUtilities.TryPrepareReplacementMeshForLiveBones(
                log,
                smr,
                originalMesh,
                effectiveReplacementMesh,
                targetBoneNames,
                replacementBoneNames,
                targetBones,
                newBones,
                out var preparedReplacementMesh))
            {
                _pinned.Add(preparedReplacementMesh);
                prepared.Mesh = preparedReplacementMesh;
                prepared.Bounds = preparedReplacementMesh.bounds;
            }
            else
            {
                prepared.Mesh = effectiveReplacementMesh;
                prepared.Bounds = effectiveReplacementMesh.bounds;
            }
        }

        _preparedAssignments[key] = prepared;
        return prepared;
    }

    public void ClearPreparedAssignments()
        => _preparedAssignments.Clear();
}
