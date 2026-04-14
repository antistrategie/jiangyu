using System.Numerics;
using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Glb;

public class IsUniformSmallScaleTests
{
    [Theory]
    [InlineData(0.01f, 0.01f, 0.01f, true)]
    [InlineData(0.019f, 0.019f, 0.019f, true)]
    [InlineData(0.005f, 0.005f, 0.005f, true)]
    public void SmallUniformScale_ReturnsTrue(float x, float y, float z, bool expected)
    {
        Assert.Equal(expected, ModelCleanupService.IsUniformSmallScale(new Vector3(x, y, z)));
    }

    [Theory]
    [InlineData(1.0f, 1.0f, 1.0f)]
    [InlineData(0.03f, 0.03f, 0.03f)]
    public void NormalOrLargeScale_ReturnsFalse(float x, float y, float z)
    {
        Assert.False(ModelCleanupService.IsUniformSmallScale(new Vector3(x, y, z)));
    }

    [Fact]
    public void BelowMinimum_ReturnsFalse()
    {
        Assert.False(ModelCleanupService.IsUniformSmallScale(new Vector3(0.0001f, 0.0001f, 0.0001f)));
    }

    [Fact]
    public void NonUniform_ReturnsFalse()
    {
        Assert.False(ModelCleanupService.IsUniformSmallScale(new Vector3(0.01f, 0.02f, 0.01f)));
    }
}

public class SnapScaleTests
{
    [Fact]
    public void NearOneValues_SnapToOne()
    {
        var result = ModelCleanupService.SnapScale(new Vector3(0.999999f, 1.000001f, 1.0f));

        Assert.Equal(1f, result.X);
        Assert.Equal(1f, result.Y);
        Assert.Equal(1f, result.Z);
    }

    [Fact]
    public void FarFromOne_Preserved()
    {
        var result = ModelCleanupService.SnapScale(new Vector3(0.5f, 2.0f, 0.01f));

        Assert.Equal(0.5f, result.X);
        Assert.Equal(2.0f, result.Y);
        Assert.Equal(0.01f, result.Z);
    }

    [Fact]
    public void ExactlyOne_Unchanged()
    {
        var result = ModelCleanupService.SnapScale(Vector3.One);

        Assert.Equal(Vector3.One, result);
    }

    [Fact]
    public void Zero_Preserved()
    {
        var result = ModelCleanupService.SnapScale(Vector3.Zero);

        Assert.Equal(Vector3.Zero, result);
    }
}
