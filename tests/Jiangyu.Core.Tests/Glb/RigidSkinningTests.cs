using AssetRipper.Numerics;

namespace Jiangyu.Core.Tests.Glb;

/// <summary>
/// Covers AssetRipper's <see cref="BoneWeight4.NormalizeWeights"/> behaviour for the
/// rigid-skinning layout: a vertex with BlendIndices populated but no BlendWeight
/// channel has Sum = 0, and must be treated as rigid (1.0 on Index0) rather than
/// getting the historic uniform 0.25/0.25/0.25/0.25 fallback that corrupts
/// downstream sparse-weight dedupe.
/// </summary>
public class BoneWeight4NormalizeWeightsRigidTests
{
    [Fact]
    public void SumZero_WithIndex_IsTreatedAsRigidOnSlotZero()
    {
        // Unity's rigid-skinning vertex layout: BlendIndices dim=1 produces a skin
        // entry with Weights all zero but a valid Index0. AssetRipper must treat
        // this as rigid (1.0 on Index0), not as a uniform 0.25/0.25/0.25/0.25 split,
        // which collapses to 25% target + 75% bone-0 after sparse-weight dedupe.
        var skin = new BoneWeight4(0f, 0f, 0f, 0f, 7, 0, 0, 0);
        var normalised = skin.NormalizeWeights();

        Assert.Equal(1f, normalised.Weight0);
        Assert.Equal(0f, normalised.Weight1);
        Assert.Equal(0f, normalised.Weight2);
        Assert.Equal(0f, normalised.Weight3);
        Assert.Equal(7, normalised.Index0);
        Assert.Equal(0, normalised.Index1);
        Assert.Equal(0, normalised.Index2);
        Assert.Equal(0, normalised.Index3);
    }

    [Fact]
    public void NonUnitSum_NormalisesProportionally()
    {
        // Genuine blended weights that don't sum to 1 must still normalise proportionally —
        // the rigid-sum-zero branch must not interfere with regular blending behaviour.
        var skin = new BoneWeight4(0.3f, 0.15f, 0.05f, 0f, 1, 2, 3, 0);
        var normalised = skin.NormalizeWeights();

        Assert.Equal(0.6f, normalised.Weight0, 4);
        Assert.Equal(0.3f, normalised.Weight1, 4);
        Assert.Equal(0.1f, normalised.Weight2, 4);
        Assert.Equal(0f, normalised.Weight3);
        Assert.Equal(1, normalised.Index0);
        Assert.Equal(2, normalised.Index1);
        Assert.Equal(3, normalised.Index2);
    }

    [Fact]
    public void UnitSum_PassesThrough()
    {
        var skin = new BoneWeight4(0.75f, 0.25f, 0f, 0f, 0, 4, 0, 0);
        var normalised = skin.NormalizeWeights();

        Assert.Equal(0.75f, normalised.Weight0, 4);
        Assert.Equal(0.25f, normalised.Weight1, 4);
        Assert.Equal(0, normalised.Index0);
        Assert.Equal(4, normalised.Index1);
    }
}
