using MelonLoader;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

internal static class MeshPreparationUtilities
{
    public static bool TargetMeshVertexCountMatches(Mesh targetMesh, Mesh replacementMesh)
        => targetMesh != null && replacementMesh != null && targetMesh.vertexCount == replacementMesh.vertexCount;

    public static bool TryCopyGeometryOntoExistingMesh(MelonLogger.Instance log, Mesh targetMesh, Mesh replacementMesh)
    {
        if (targetMesh == null || replacementMesh == null)
            return false;
        if (!replacementMesh.isReadable)
        {
            log.Warning("  [DIAG] replacement mesh is not readable; cannot copy geometry");
            return false;
        }

        try
        {
            var vertices = replacementMesh.vertices;
            var normals = replacementMesh.normals;
            var tangents = replacementMesh.tangents;
            var uv = replacementMesh.uv;
            var uv2 = replacementMesh.uv2;
            var colors32 = replacementMesh.colors32;
            var triangles = replacementMesh.triangles;

            targetMesh.Clear();
            targetMesh.indexFormat = replacementMesh.indexFormat;
            targetMesh.vertices = vertices;
            if (normals != null && normals.Length == vertices.Length)
                targetMesh.normals = normals;
            if (tangents != null && tangents.Length == vertices.Length)
                targetMesh.tangents = tangents;
            if (uv != null && uv.Length == vertices.Length)
                targetMesh.uv = uv;
            if (uv2 != null && uv2.Length == vertices.Length)
                targetMesh.uv2 = uv2;
            if (colors32 != null && colors32.Length == vertices.Length)
                targetMesh.colors32 = colors32;
            targetMesh.triangles = triangles;
            targetMesh.RecalculateBounds();

            log.Msg($"  [DIAG] in-place geometry copy ok verts={vertices.Length} tris={triangles.Length}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"  [DIAG] in-place geometry copy failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static Mesh InstantiateMeshClone(Mesh sourceMesh)
    {
        var clone = UnityEngine.Object.Instantiate(sourceMesh);
        clone.name = $"{sourceMesh.name} [jiangyu]";
        return clone;
    }

    public static bool TryPrepareReplacementMeshForLiveBones(
        MelonLogger.Instance log,
        SkinnedMeshRenderer smr,
        Mesh targetMesh,
        Mesh replacementMesh,
        string[] targetBoneNames,
        string[] replacementBoneNames,
        Transform[] targetBones,
        Transform[] newBones,
        out Mesh preparedMesh)
    {
        preparedMesh = null;
        if (replacementMesh == null)
            return false;

        try
        {
            var runtimeMesh = InstantiateMeshClone(replacementMesh);

            // The live target mesh's bindposes are the runtime-authoritative truth:
            // they are paired with the live bone transforms the scene actually has.
            // The compiled bindposes in the replacement can drift from live (ModelCleanupService
            // snap-scaling joint transforms, cleaned-glTF re-derivation from joint world matrices),
            // and that drift amplifies into visible orbit as bones animate. Prefer aligned
            // live bindposes; fall back to compiled authored bindposes only if the target
            // has no readable bindposes; fall back to rebuilding from current live bone
            // world transforms as a last resort.
            Matrix4x4[] bindPosesToUse = null;
            if (TryReadNativeBindPoses(log, targetMesh, out var targetBindPoses) &&
                targetBindPoses != null &&
                targetBoneNames != null &&
                targetBoneNames.Length == targetBindPoses.Length)
            {
                bindPosesToUse = AlignBindPosesToReplacementOrder(
                    log,
                    smr,
                    targetBindPoses,
                    targetBoneNames,
                    replacementBoneNames,
                    targetBones,
                    newBones);
            }

            if (bindPosesToUse == null &&
                TryReadNativeBindPoses(log, replacementMesh, out var replacementBindPoses) &&
                replacementBindPoses != null &&
                replacementBindPoses.Length == newBones.Length)
            {
                bindPosesToUse = replacementBindPoses;
            }

            bindPosesToUse ??= BuildBindPosesFromLiveBones(smr.transform, newBones);

            runtimeMesh.bindposes = bindPosesToUse;
            preparedMesh = runtimeMesh;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to prepare replacement bindposes for '{replacementMesh.name}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static Matrix4x4[] AlignBindPosesToReplacementOrder(
        MelonLogger.Instance log,
        SkinnedMeshRenderer smr,
        Matrix4x4[] targetBindPoses,
        string[] targetBoneNames,
        string[] replacementBoneNames,
        Transform[] targetBones,
        Transform[] newBones)
    {
        if (targetBindPoses == null ||
            targetBoneNames == null ||
            replacementBoneNames == null ||
            targetBones == null ||
            newBones == null)
        {
            return null;
        }

        if (replacementBoneNames.Length != newBones.Length)
        {
            return null;
        }

        var aligned = new Matrix4x4[replacementBoneNames.Length];
        var mapped = new bool[aligned.Length];
        var fallbackBoneNames = new List<string>();

        if (targetBindPoses.Length == targetBones.Length && targetBones.Length > 0)
        {
            var targetBindPosesByBoneInstanceId = new Dictionary<int, Matrix4x4>();
            for (int i = 0; i < targetBones.Length; i++)
            {
                var bone = targetBones[i];
                if (bone == null)
                    continue;

                targetBindPosesByBoneInstanceId.TryAdd(bone.GetInstanceID(), targetBindPoses[i]);
            }

            for (int i = 0; i < replacementBoneNames.Length; i++)
            {
                var mappedBone = newBones[i];
                if (mappedBone == null)
                    continue;

                if (targetBindPosesByBoneInstanceId.TryGetValue(mappedBone.GetInstanceID(), out var bindPose))
                {
                    aligned[i] = bindPose;
                    mapped[i] = true;
                }
            }
        }

        if (targetBindPoses.Length == targetBoneNames.Length && targetBoneNames.Length > 0)
        {
            var targetBindPosesByBoneName = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            for (int i = 0; i < targetBoneNames.Length; i++)
            {
                var boneName = targetBoneNames[i];
                if (string.IsNullOrWhiteSpace(boneName))
                    continue;

                targetBindPosesByBoneName.TryAdd(boneName, targetBindPoses[i]);
            }

            for (int i = 0; i < replacementBoneNames.Length; i++)
            {
                if (mapped[i])
                    continue;

                var replacementBoneName = replacementBoneNames[i];
                if (!string.IsNullOrWhiteSpace(replacementBoneName) &&
                    targetBindPosesByBoneName.TryGetValue(replacementBoneName, out var bindPose))
                {
                    aligned[i] = bindPose;
                    mapped[i] = true;
                }
            }
        }

        var rendererLocalToWorld = smr.transform.localToWorldMatrix;
        for (int i = 0; i < replacementBoneNames.Length; i++)
        {
            if (mapped[i])
                continue;

            var replacementBoneName = replacementBoneNames[i];
            var mappedBone = i < newBones.Length ? newBones[i] : null;
            if (mappedBone != null)
            {
                aligned[i] = mappedBone.worldToLocalMatrix * rendererLocalToWorld;
            }
            else
            {
                aligned[i] = Matrix4x4.identity;
            }

            if (!string.IsNullOrWhiteSpace(replacementBoneName))
                fallbackBoneNames.Add(replacementBoneName);
        }

        if (fallbackBoneNames.Count > 0)
        {
            log.Warning(
                $"  [DIAG] replacement bind-pose alignment used live fallback for {fallbackBoneNames.Count} bone(s): {FormatNameListPreview(fallbackBoneNames, 12)}");
        }

        return aligned;
    }

    public static bool TryUseReplacementMeshDirectly(MelonLogger.Instance log, Mesh targetMesh, Mesh replacementMesh, string[] targetBoneNames, string[] replacementBoneNames)
    {
        if (targetMesh == null || replacementMesh == null)
            return false;
        if (targetBoneNames == null || replacementBoneNames == null)
            return false;
        if (targetBoneNames.Length != replacementBoneNames.Length)
            return false;

        for (int i = 0; i < targetBoneNames.Length; i++)
        {
            if (!string.Equals(targetBoneNames[i], replacementBoneNames[i], StringComparison.Ordinal))
                return false;
        }

        if (!TryReadNativeBindPoses(log, targetMesh, out var targetBindPoses) || targetBindPoses == null)
            return false;
        if (!TryReadNativeBindPoses(log, replacementMesh, out var replacementBindPoses) || replacementBindPoses == null)
            return false;

        return BindPosesMatch(targetBindPoses, replacementBindPoses);
    }

    private static Matrix4x4[] BuildBindPosesFromLiveBones(Transform rendererTransform, Transform[] bones)
    {
        var bindPoses = new Matrix4x4[bones.Length];
        var rendererLocalToWorld = rendererTransform.localToWorldMatrix;
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            bindPoses[i] = bone != null
                ? bone.worldToLocalMatrix * rendererLocalToWorld
                : Matrix4x4.identity;
        }

        return bindPoses;
    }

    private static bool TryReadNativeBindPoses(MelonLogger.Instance log, Mesh sourceMesh, out Matrix4x4[] bindPoses)
    {
        bindPoses = null;
        if (sourceMesh == null)
            return false;

        try
        {
            var count = sourceMesh.bindposeCount;
            if (count <= 0)
                return false;

            var ptr = sourceMesh.GetBindposesArray();
            if (ptr == IntPtr.Zero)
                return false;

            bindPoses = new Matrix4x4[count];
            var stride = Marshal.SizeOf<Matrix4x4>();
            for (int i = 0; i < count; i++)
                bindPoses[i] = Marshal.PtrToStructure<Matrix4x4>(IntPtr.Add(ptr, i * stride));
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to read native bindposes from '{sourceMesh.name}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool BindPosesMatch(Matrix4x4[] a, Matrix4x4[] b, float epsilon = 1e-4f)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            var ma = a[i];
            var mb = b[i];
            if (Math.Abs(ma.m00 - mb.m00) > epsilon || Math.Abs(ma.m01 - mb.m01) > epsilon || Math.Abs(ma.m02 - mb.m02) > epsilon || Math.Abs(ma.m03 - mb.m03) > epsilon ||
                Math.Abs(ma.m10 - mb.m10) > epsilon || Math.Abs(ma.m11 - mb.m11) > epsilon || Math.Abs(ma.m12 - mb.m12) > epsilon || Math.Abs(ma.m13 - mb.m13) > epsilon ||
                Math.Abs(ma.m20 - mb.m20) > epsilon || Math.Abs(ma.m21 - mb.m21) > epsilon || Math.Abs(ma.m22 - mb.m22) > epsilon || Math.Abs(ma.m23 - mb.m23) > epsilon ||
                Math.Abs(ma.m30 - mb.m30) > epsilon || Math.Abs(ma.m31 - mb.m31) > epsilon || Math.Abs(ma.m32 - mb.m32) > epsilon || Math.Abs(ma.m33 - mb.m33) > epsilon)
            {
                return false;
            }
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
}
