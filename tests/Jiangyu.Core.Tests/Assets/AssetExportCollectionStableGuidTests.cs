using AssetRipper.Assets;
using AssetRipper.Export.UnityProjects;

namespace Jiangyu.Core.Tests.Assets;

public class AssetExportCollectionStableGuidTests
{
    [Fact]
    public void SameInputsProduceSameGuid()
    {
        var a = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 42, 28);
        var b = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 42, 28);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentCollectionNameProducesDifferentGuid()
    {
        var a = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 42, 28);
        var b = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("other", 42, 28);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentPathIdProducesDifferentGuid()
    {
        var a = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 42, 28);
        var b = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 43, 28);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentClassIdProducesDifferentGuid()
    {
        var a = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 42, 28);
        var b = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("bundle", 42, 21);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EmptyCollectionNameIsStillDeterministic()
    {
        var a = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("", 42, 28);
        var b = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("", 42, 28);
        Assert.Equal(a, b);
    }
}
