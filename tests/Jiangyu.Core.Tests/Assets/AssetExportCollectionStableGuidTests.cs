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

    // A shared source asset is exported into one subset directory per prefab, so its
    // identity (collection name + path id + class id) is identical across those copies.
    // ComputeStableGuid(T) salts the collection name with ExportCollection.GuidNamespace
    // (the destination prefab) so the copies get distinct but rip-stable GUIDs; without
    // it Unity sees duplicate GUIDs on import and reassigns random ones every rip.
    [Fact]
    public void DifferentNamespaceSaltProducesDistinctButStableGuid()
    {
        var arc = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("arc/resources.assets", 7692, 48);
        var sniper = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("sniper/resources.assets", 7692, 48);
        var arcAgain = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("arc/resources.assets", 7692, 48);

        Assert.NotEqual(arc, sniper);
        Assert.Equal(arc, arcAgain);
    }
}
