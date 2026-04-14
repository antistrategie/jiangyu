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

    public static bool TryPrepareReplacementMeshForLiveBones(MelonLogger.Instance log, SkinnedMeshRenderer smr, Mesh targetMesh, Mesh replacementMesh, Transform[] newBones, out Mesh preparedMesh)
    {
        preparedMesh = null;
        if (replacementMesh == null)
            return false;

        try
        {
            var runtimeMesh = InstantiateMeshClone(replacementMesh);
            if (!TryReadNativeBindPoses(log, targetMesh, out var bindPosesToUse) || bindPosesToUse == null || bindPosesToUse.Length != newBones.Length)
            {
                if (TryReadNativeBindPoses(log, replacementMesh, out var replacementBindPoses) &&
                    replacementBindPoses != null &&
                    replacementBindPoses.Length == newBones.Length)
                {
                    bindPosesToUse = replacementBindPoses;
                }
            }

            if (bindPosesToUse == null || bindPosesToUse.Length != newBones.Length)
                bindPosesToUse = BuildBindPosesFromLiveBones(smr.transform, newBones);

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
}
