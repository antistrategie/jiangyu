using Jiangyu.Loader.Replacements;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class LodReplacementResolverTests
{
    [Fact]
    public void FindNearestAvailableTarget_UsesExactMatchFirst()
    {
        var result = LodReplacementResolver.FindNearestAvailableTarget(
            ["soldier_LOD0", "soldier_LOD2"],
            "soldier_LOD2");

        Assert.Equal("soldier_LOD2", result);
    }

    [Fact]
    public void FindNearestAvailableTarget_UsesNearestAvailableLod()
    {
        var result = LodReplacementResolver.FindNearestAvailableTarget(
            ["soldier_LOD0", "soldier_LOD2"],
            "soldier_LOD1");

        Assert.Equal("soldier_LOD0", result);
    }

    [Fact]
    public void FindNearestAvailableTarget_UsesOnlyAvailableLodForEntireFamily()
    {
        var result = LodReplacementResolver.FindNearestAvailableTarget(
            ["soldier_LOD2"],
            "soldier_LOD0");

        Assert.Equal("soldier_LOD2", result);
    }

    [Fact]
    public void FindNearestAvailableTarget_DoesNotCrossFamilies()
    {
        var result = LodReplacementResolver.FindNearestAvailableTarget(
            ["soldier_LOD0", "tank_LOD0"],
            "walker_LOD1");

        Assert.Null(result);
    }

    [Fact]
    public void TryParseLodName_ParsesBaseNameAndIndex()
    {
        var success = LodReplacementResolver.TryParseLodName("local_forces_basic_soldier_LOD3", out var baseName, out var lodIndex);

        Assert.True(success);
        Assert.Equal("local_forces_basic_soldier", baseName);
        Assert.Equal(3, lodIndex);
    }
}
