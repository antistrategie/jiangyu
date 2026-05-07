using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests for the pure-logic helpers on
/// <see cref="Il2CppMdArrayAccessor"/>. The native field-pointer and
/// memory-read paths can only be exercised with a live IL2CPP runtime
/// (verified end-to-end via in-game smoke). The row-major index math
/// has no IL2CPP dependencies and can be unit-tested directly — this
/// file locks in the off-by-one and ordering invariants the field code
/// relies on.
/// </summary>
public class Il2CppMdArrayAccessorTests
{
    [Fact]
    public void TryComputeRowMajorIndex_2D_BasicCells()
    {
        // 9x9 grid (AOETiles shape). Standard row-major: idx = r*9 + c.
        var dims = new[] { 9, 9 };

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 0 }, dims, out var i0, out _));
        Assert.Equal(0, i0);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 8 }, dims, out var i1, out _));
        Assert.Equal(8, i1);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 1, 0 }, dims, out var i2, out _));
        Assert.Equal(9, i2);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 4, 4 }, dims, out var i3, out _));
        Assert.Equal(40, i3);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 8, 8 }, dims, out var i4, out _));
        Assert.Equal(80, i4);
    }

    [Fact]
    public void TryComputeRowMajorIndex_3D_StridesCorrectly()
    {
        // 3x4x5: idx = r*(4*5) + c*5 + d.
        var dims = new[] { 3, 4, 5 };

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 0, 0 }, dims, out var i0, out _));
        Assert.Equal(0, i0);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 0, 4 }, dims, out var i1, out _));
        Assert.Equal(4, i1);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 1, 0 }, dims, out var i2, out _));
        Assert.Equal(5, i2);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 1, 0, 0 }, dims, out var i3, out _));
        Assert.Equal(20, i3);

        Assert.True(Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 2, 3, 4 }, dims, out var i4, out _));
        Assert.Equal(2 * 4 * 5 + 3 * 5 + 4, i4);
    }

    [Fact]
    public void TryComputeRowMajorIndex_RankMismatch_Errors()
    {
        var dims = new[] { 9, 9 };
        var ok = Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 0, 0 }, dims, out var idx, out var error);

        Assert.False(ok);
        Assert.Equal(0, idx);
        Assert.Contains("rank", error);
        Assert.Contains("3", error);
        Assert.Contains("2", error);
    }

    [Fact]
    public void TryComputeRowMajorIndex_NegativeCoord_Errors()
    {
        var dims = new[] { 9, 9 };
        var ok = Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { -1, 0 }, dims, out _, out var error);

        Assert.False(ok);
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void TryComputeRowMajorIndex_CoordEqualToLength_Errors()
    {
        // 9x9 — valid coords are 0..8. Index 9 is one past the end, a
        // common off-by-one mistake when modders translate a "9th
        // element" mental model into KDL.
        var dims = new[] { 9, 9 };
        var ok = Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 0, 9 }, dims, out _, out var error);

        Assert.False(ok);
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void TryComputeRowMajorIndex_CoordPastLength_Errors()
    {
        var dims = new[] { 9, 9 };
        var ok = Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
            new[] { 5, 100 }, dims, out _, out var error);

        Assert.False(ok);
        Assert.Contains("out of range", error);
    }
}
