using System.Numerics;
using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Glb;

public sealed class BindPoseRetargetServiceTests
{
    [Fact]
    public void IdentityContracts_DoNotNeedRetarget()
    {
        var contract = CreateTwoBoneContract(Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, -1f, 0f));

        Assert.False(BindPoseRetargetService.NeedsRetarget(contract, contract));
    }

    [Fact]
    public void IdentityRetarget_LeavesVertexDataUnchanged()
    {
        var contract = CreateTwoBoneContract(Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, -1f, 0f));
        var positions = new[] { new Vector3(0f, 1.5f, 0f) };
        var normals = new[] { Vector3.UnitY };
        var tangents = new[] { new Vector4(Vector3.UnitX, 1f) };
        var weights = new[] { 1f, 0f, 0f, 0f };
        var indices = new[] { 0, 0, 0, 0 };

        var result = BindPoseRetargetService.Retarget(positions, normals, tangents, weights, indices, contract, contract);

        Assert.Equal(positions[0], result.Positions[0]);
        Assert.Equal(normals[0], result.Normals[0]);
        Assert.Equal(tangents[0], result.Tangents[0]);
        Assert.Equal(contract.BindPoses[0], result.BindPoses[0]);
        Assert.Equal(contract.BindPoses[1], result.BindPoses[1]);
    }

    [Fact]
    public void ProportionRetarget_RepositionsVertexAgainstTargetRestPose()
    {
        var authored = CreateTwoBoneContract(
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(0f, -1.5f, 0f));
        var target = CreateTwoBoneContract(
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(0f, -1f, 0f));

        var positions = new[] { new Vector3(0f, 1.8f, 0f) };
        var normals = new[] { Vector3.UnitY };
        var tangents = new[] { new Vector4(Vector3.UnitX, 1f) };
        var weights = new[] { 0f, 1f, 0f, 0f };
        var indices = new[] { 0, 1, 0, 0 };

        var result = BindPoseRetargetService.Retarget(positions, normals, tangents, weights, indices, authored, target);

        Assert.True(Math.Abs(result.Positions[0].Y - 1.3f) < 1e-4f);
        Assert.Equal(target.BindPoses[1], result.BindPoses[1]);
    }

    [Fact]
    public void RotationRetarget_FullyWeightedVertex_MatchesTargetAnimation()
    {
        var targetWorld = CreateTwoBoneWorldRest(childRotationDegrees: 0f);
        var authoredWorld = CreateTwoBoneWorldRest(childRotationDegrees: 45f);
        var target = CreateContractFromWorldRest(targetWorld);
        var authored = CreateContractFromWorldRest(authoredWorld);

        var targetVertex = new Vector3(0.2f, 1.4f, 0f);
        var weights = new[] { 0f, 1f, 0f, 0f };
        var indices = new[] { 0, 1, 0, 0 };
        var authoredVertex = ReposeVertex(targetVertex, targetWorld, target.BindPoses, authoredWorld, weights, indices);

        var result = BindPoseRetargetService.Retarget([authoredVertex], [], [], weights, indices, authored, target);
        AssertNearlyEqual(targetVertex, result.Positions[0], 1e-4f);

        var animatedTargetWorld = CreateTwoBoneAnimatedPose(childRotationDegrees: 25f);
        var expected = SkinVertex(targetVertex, animatedTargetWorld, target.BindPoses, weights, indices);
        var actual = SkinVertex(result.Positions[0], animatedTargetWorld, target.BindPoses, weights, indices);

        AssertNearlyEqual(expected, actual, 1e-4f);
    }

    [Fact]
    public void RotationRetarget_MixedWeightVertex_MatchesTargetAnimation()
    {
        var targetWorld = CreateTwoBoneWorldRest(childRotationDegrees: 0f);
        var authoredWorld = CreateTwoBoneWorldRest(childRotationDegrees: 45f);
        var target = CreateContractFromWorldRest(targetWorld);
        var authored = CreateContractFromWorldRest(authoredWorld);

        var targetVertex = new Vector3(0.15f, 0.95f, 0f);
        var weights = new[] { 0.5f, 0.5f, 0f, 0f };
        var indices = new[] { 0, 1, 0, 0 };
        var authoredVertex = ReposeVertex(targetVertex, targetWorld, target.BindPoses, authoredWorld, weights, indices);

        var result = BindPoseRetargetService.Retarget([authoredVertex], [], [], weights, indices, authored, target);

        // Mixed-weight recovery cannot be exact under LBS when authored and target joint rotations differ:
        // the service blends per-bone recoveries, so (0.5·I + 0.5·F⁻¹)·(0.5·I + 0.5·F) ≠ I for F ≠ I.
        // The real claim is that the animated output stays close to what the target skeleton would produce;
        // epsilon reflects the LBS candy-wrapper bound for this configuration, not a precision goal.
        var animatedTargetWorld = CreateTwoBoneAnimatedPose(childRotationDegrees: 25f);
        var expected = SkinVertex(targetVertex, animatedTargetWorld, target.BindPoses, weights, indices);
        var actual = SkinVertex(result.Positions[0], animatedTargetWorld, target.BindPoses, weights, indices);

        AssertNearlyEqual(expected, actual, 5e-2f);
    }

    [Fact]
    public void HierarchyMismatch_ThrowsClearError()
    {
        var authored = new BindPoseRetargetService.SkeletonContract
        {
            BoneNames = ["Root", "Child"],
            ParentNames = [null, "Root"],
            BindPoses = [Matrix4x4.Identity, Matrix4x4.Identity],
        };
        var target = new BindPoseRetargetService.SkeletonContract
        {
            BoneNames = ["Root", "Child"],
            ParentNames = [null, null],
            BindPoses = [Matrix4x4.Identity, Matrix4x4.Identity],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BindPoseRetargetService.NeedsRetarget(authored, target));
        Assert.Contains("same bone hierarchy", ex.Message);
    }

    private static BindPoseRetargetService.SkeletonContract CreateTwoBoneContract(Matrix4x4 rootBindPose, Matrix4x4 childBindPose)
    {
        return new BindPoseRetargetService.SkeletonContract
        {
            BoneNames = ["Root", "Child"],
            ParentNames = [null, "Root"],
            BindPoses = [rootBindPose, childBindPose],
        };
    }

    private static BindPoseRetargetService.SkeletonContract CreateContractFromWorldRest(IReadOnlyList<Matrix4x4> worldRest)
    {
        return new BindPoseRetargetService.SkeletonContract
        {
            BoneNames = ["Root", "Child"],
            ParentNames = [null, "Root"],
            BindPoses =
            [
                Matrix4x4.Invert(worldRest[0], out var rootBind) ? rootBind : throw new InvalidOperationException(),
                Matrix4x4.Invert(worldRest[1], out var childBind) ? childBind : throw new InvalidOperationException(),
            ],
        };
    }

    private static Matrix4x4[] CreateTwoBoneWorldRest(float childRotationDegrees)
    {
        var rootWorld = Matrix4x4.Identity;
        var childLocal = Matrix4x4.CreateRotationZ(MathF.PI * childRotationDegrees / 180f) *
                         Matrix4x4.CreateTranslation(0f, 1f, 0f);
        var childWorld = childLocal * rootWorld;
        return [rootWorld, childWorld];
    }

    private static Matrix4x4[] CreateTwoBoneAnimatedPose(float childRotationDegrees)
        => CreateTwoBoneWorldRest(childRotationDegrees);

    private static Vector3 ReposeVertex(
        Vector3 sourceVertex,
        IReadOnlyList<Matrix4x4> sourceWorldRest,
        IReadOnlyList<Matrix4x4> sourceBindPoses,
        IReadOnlyList<Matrix4x4> targetWorldRest,
        IReadOnlyList<float> weights,
        IReadOnlyList<int> indices)
    {
        var blended = ZeroMatrix();
        for (int i = 0; i < 4; i++)
        {
            var weight = weights[i];
            if (weight <= 0f)
                continue;

            var jointIndex = indices[i];
            var reposer = sourceBindPoses[jointIndex] * targetWorldRest[jointIndex];
            AddWeighted(ref blended, reposer, weight);
        }

        return Vector3.Transform(sourceVertex, blended);
    }

    private static Vector3 SkinVertex(
        Vector3 sourceVertex,
        IReadOnlyList<Matrix4x4> worldTransforms,
        IReadOnlyList<Matrix4x4> bindPoses,
        IReadOnlyList<float> weights,
        IReadOnlyList<int> indices)
    {
        var blended = ZeroMatrix();
        for (int i = 0; i < 4; i++)
        {
            var weight = weights[i];
            if (weight <= 0f)
                continue;

            var jointIndex = indices[i];
            var skinMatrix = bindPoses[jointIndex] * worldTransforms[jointIndex];
            AddWeighted(ref blended, skinMatrix, weight);
        }

        return Vector3.Transform(sourceVertex, blended);
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

    private static void AssertNearlyEqual(Vector3 expected, Vector3 actual, float epsilon)
    {
        Assert.True(Vector3.Distance(expected, actual) < epsilon,
            $"Expected {expected}, got {actual}.");
    }
}
