using System.Numerics;

namespace Jiangyu.Core.Glb;

public static class BindPoseRetargetService
{
    public sealed class SkeletonContract
    {
        public required string[] BoneNames { get; init; }
        public required string?[] ParentNames { get; init; }
        public required Matrix4x4[] BindPoses { get; init; }
    }

    public sealed class RetargetResult
    {
        public required Vector3[] Positions { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Vector4[] Tangents { get; init; }
        public required Matrix4x4[] BindPoses { get; init; }
    }

    public static bool NeedsRetarget(SkeletonContract authored, SkeletonContract target, float epsilon = 1e-4f)
    {
        ValidateContracts(authored, target);

        for (int i = 0; i < authored.BindPoses.Length; i++)
        {
            var targetIndex = FindTargetIndex(target, authored.BoneNames[i]);
            if (!MatricesApproximatelyEqual(authored.BindPoses[i], target.BindPoses[targetIndex], epsilon))
                return true;
        }

        return false;
    }

    public static RetargetResult Retarget(
        Vector3[] positions,
        Vector3[] normals,
        Vector4[] tangents,
        float[] boneWeights,
        int[] boneIndices,
        SkeletonContract authored,
        SkeletonContract target)
    {
        if (positions.Length * 4 != boneWeights.Length || positions.Length * 4 != boneIndices.Length)
            throw new InvalidOperationException("Bind-pose retargeting requires one 4-weight/4-index set per vertex.");
        if (normals.Length != 0 && normals.Length != positions.Length)
            throw new InvalidOperationException("Normal count must match position count for bind-pose retargeting.");
        if (tangents.Length != 0 && tangents.Length != positions.Length)
            throw new InvalidOperationException("Tangent count must match position count for bind-pose retargeting.");

        ValidateContracts(authored, target);

        // Per-bone recovery: IBM_auth * BP_target in row-vec form. This is the exact
        // transform that takes an authored-space vertex to target-space assuming full
        // weight on that one bone. We blend these recoveries per vertex via standard
        // LBS weighting — blending *forwards* and inverting afterwards explodes at
        // joints where authored and target bone orientations differ, because the
        // blended forward matrix can be near-singular.
        var targetBindPoses = AlignTargetBindPoses(target, authored);
        var recoveryMatrices = new Matrix4x4[targetBindPoses.Length];
        for (int i = 0; i < targetBindPoses.Length; i++)
        {
            if (!Matrix4x4.Invert(authored.BindPoses[i], out var authoredRest))
                throw new InvalidOperationException($"Authored bind pose for bone '{authored.BoneNames[i]}' is not invertible.");

            var forward = targetBindPoses[i] * authoredRest;
            if (!Matrix4x4.Invert(forward, out var recovery))
                throw new InvalidOperationException($"Bind-pose retargeting produced a non-invertible per-bone repose matrix for bone '{authored.BoneNames[i]}'.");

            recoveryMatrices[i] = recovery;
        }

        var retargetedPositions = new Vector3[positions.Length];
        var retargetedNormals = normals.Length == 0 ? [] : new Vector3[normals.Length];
        var retargetedTangents = tangents.Length == 0 ? [] : new Vector4[tangents.Length];

        for (int vertexIndex = 0; vertexIndex < positions.Length; vertexIndex++)
        {
            var blendedRecovery = BlendWeightedMatrix(recoveryMatrices, boneWeights, boneIndices, vertexIndex);

            retargetedPositions[vertexIndex] = Vector3.Transform(positions[vertexIndex], blendedRecovery);

            if (normals.Length != 0)
            {
                var normalMatrix = BuildNormalMatrix(blendedRecovery);
                retargetedNormals[vertexIndex] = Vector3.Normalize(Vector3.TransformNormal(normals[vertexIndex], normalMatrix));
            }

            if (tangents.Length != 0)
            {
                var tangent = tangents[vertexIndex];
                var tangentDir = new Vector3(tangent.X, tangent.Y, tangent.Z);
                var tangentMatrix = BuildNormalMatrix(blendedRecovery);
                var retargetedDir = Vector3.Normalize(Vector3.TransformNormal(tangentDir, tangentMatrix));
                retargetedTangents[vertexIndex] = new Vector4(retargetedDir, tangent.W);
            }
        }

        return new RetargetResult
        {
            Positions = retargetedPositions,
            Normals = retargetedNormals,
            Tangents = retargetedTangents,
            BindPoses = targetBindPoses,
        };
    }

    private static void ValidateContracts(SkeletonContract authored, SkeletonContract target)
    {
        if (authored.BoneNames.Length != authored.ParentNames.Length || authored.BoneNames.Length != authored.BindPoses.Length)
            throw new InvalidOperationException("Authored bind-pose contract is internally inconsistent.");
        if (target.BoneNames.Length != target.ParentNames.Length || target.BoneNames.Length != target.BindPoses.Length)
            throw new InvalidOperationException("Target bind-pose contract is internally inconsistent.");

        foreach (var boneName in authored.BoneNames)
        {
            _ = FindTargetIndex(target, boneName);
        }

        for (int i = 0; i < authored.BoneNames.Length; i++)
        {
            var boneName = authored.BoneNames[i];
            var targetIndex = FindTargetIndex(target, boneName);
            var authoredParent = authored.ParentNames[i];
            var targetParent = target.ParentNames[targetIndex];
            if (!string.Equals(authoredParent, targetParent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Bind-pose retargeting requires the same bone hierarchy. Bone '{boneName}' has parent '{authoredParent ?? "<root>"}' in the authored model and '{targetParent ?? "<root>"}' in the reference skeleton.");
            }
        }
    }

    private static int FindTargetIndex(SkeletonContract target, string boneName)
    {
        for (int i = 0; i < target.BoneNames.Length; i++)
        {
            if (string.Equals(target.BoneNames[i], boneName, StringComparison.Ordinal))
                return i;
        }

        throw new InvalidOperationException(
            $"Bind-pose retargeting requires the same bone names. Bone '{boneName}' was not found in the reference skeleton.");
    }

    private static Matrix4x4[] AlignTargetBindPoses(SkeletonContract target, SkeletonContract authored)
    {
        var aligned = new Matrix4x4[authored.BoneNames.Length];
        for (int i = 0; i < authored.BoneNames.Length; i++)
        {
            var targetIndex = FindTargetIndex(target, authored.BoneNames[i]);
            aligned[i] = target.BindPoses[targetIndex];
        }

        return aligned;
    }

    private static Matrix4x4 BlendWeightedMatrix(
        IReadOnlyList<Matrix4x4> matrices,
        IReadOnlyList<float> boneWeights,
        IReadOnlyList<int> boneIndices,
        int vertexIndex)
    {
        var offset = vertexIndex * 4;
        var blended = ZeroMatrix();

        for (int i = 0; i < 4; i++)
        {
            var weight = boneWeights[offset + i];
            if (weight <= 0f)
                continue;

            var boneIndex = boneIndices[offset + i];
            if (boneIndex < 0 || boneIndex >= matrices.Count)
                throw new InvalidOperationException($"Bind-pose retargeting encountered invalid bone index {boneIndex}.");

            AddWeighted(ref blended, matrices[boneIndex], weight);
        }

        return blended;
    }

    private static Matrix4x4 BuildNormalMatrix(Matrix4x4 matrix)
    {
        if (Matrix4x4.Invert(matrix, out var inverse))
            return Matrix4x4.Transpose(inverse);

        return matrix;
    }

    private static bool MatricesApproximatelyEqual(Matrix4x4 left, Matrix4x4 right, float epsilon)
    {
        return MathF.Abs(left.M11 - right.M11) < epsilon &&
               MathF.Abs(left.M12 - right.M12) < epsilon &&
               MathF.Abs(left.M13 - right.M13) < epsilon &&
               MathF.Abs(left.M14 - right.M14) < epsilon &&
               MathF.Abs(left.M21 - right.M21) < epsilon &&
               MathF.Abs(left.M22 - right.M22) < epsilon &&
               MathF.Abs(left.M23 - right.M23) < epsilon &&
               MathF.Abs(left.M24 - right.M24) < epsilon &&
               MathF.Abs(left.M31 - right.M31) < epsilon &&
               MathF.Abs(left.M32 - right.M32) < epsilon &&
               MathF.Abs(left.M33 - right.M33) < epsilon &&
               MathF.Abs(left.M34 - right.M34) < epsilon &&
               MathF.Abs(left.M41 - right.M41) < epsilon &&
               MathF.Abs(left.M42 - right.M42) < epsilon &&
               MathF.Abs(left.M43 - right.M43) < epsilon &&
               MathF.Abs(left.M44 - right.M44) < epsilon;
    }

    private static Matrix4x4 ZeroMatrix() => new(
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0);

    private static void AddWeighted(ref Matrix4x4 accumulator, Matrix4x4 matrix, float weight)
    {
        accumulator.M11 += matrix.M11 * weight;
        accumulator.M12 += matrix.M12 * weight;
        accumulator.M13 += matrix.M13 * weight;
        accumulator.M14 += matrix.M14 * weight;
        accumulator.M21 += matrix.M21 * weight;
        accumulator.M22 += matrix.M22 * weight;
        accumulator.M23 += matrix.M23 * weight;
        accumulator.M24 += matrix.M24 * weight;
        accumulator.M31 += matrix.M31 * weight;
        accumulator.M32 += matrix.M32 * weight;
        accumulator.M33 += matrix.M33 * weight;
        accumulator.M34 += matrix.M34 * weight;
        accumulator.M41 += matrix.M41 * weight;
        accumulator.M42 += matrix.M42 * weight;
        accumulator.M43 += matrix.M43 * weight;
        accumulator.M44 += matrix.M44 * weight;
    }
}
