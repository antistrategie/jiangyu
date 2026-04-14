using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal sealed class MeshPreparationService
{
    private readonly List<UnityEngine.Object> _pinned;
    private readonly Dictionary<int, PreparedMeshAssignment> _preparedAssignments = new();

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
        Transform[] newBones)
    {
        var originalMesh = smr.sharedMesh;
        var originalMeshId = originalMesh.GetInstanceID();
        if (_preparedAssignments.TryGetValue(originalMeshId, out var prepared))
            return prepared;

        prepared = new PreparedMeshAssignment
        {
            UpdateWhenOffscreen = true,
        };

        var replacementHasSkinningMetadata = replacement.BoneNames != null && replacement.BoneNames.Length > 0;
        if (!replacementHasSkinningMetadata && MeshPreparationUtilities.TargetMeshVertexCountMatches(originalMesh, replacement.Mesh))
        {
            var runtimeMesh = MeshPreparationUtilities.InstantiateMeshClone(originalMesh);
            _pinned.Add(runtimeMesh);

            if (MeshPreparationUtilities.TryCopyGeometryOntoExistingMesh(log, runtimeMesh, replacement.Mesh))
            {
                prepared.Mesh = runtimeMesh;
                prepared.Bounds = runtimeMesh.bounds;
                log.Msg($"  Prepared runtime mesh clone: {runtimeMesh.name} from {originalMesh.name}");
            }
        }

        if (prepared.Mesh == null &&
            MeshPreparationUtilities.TryUseReplacementMeshDirectly(log, originalMesh, replacement.Mesh, targetBoneNames, replacementBoneNames))
        {
            prepared.Mesh = replacement.Mesh;
            prepared.Bounds = replacement.Mesh.bounds;
        }

        if (prepared.Mesh == null)
        {
            if (MeshPreparationUtilities.TryPrepareReplacementMeshForLiveBones(log, smr, originalMesh, replacement.Mesh, newBones, out var preparedReplacementMesh))
            {
                _pinned.Add(preparedReplacementMesh);
                prepared.Mesh = preparedReplacementMesh;
                prepared.Bounds = preparedReplacementMesh.bounds;
            }
            else
            {
                prepared.Mesh = replacement.Mesh;
                prepared.Bounds = replacement.Mesh.bounds;
            }
        }

        _preparedAssignments[originalMeshId] = prepared;
        return prepared;
    }
}
