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

    // Name-keyed GUIDs exist so mod content committed to a repo survives game updates:
    // a game update re-serialises the source files and reshuffles PathIDs, so any
    // PathID-derived GUID changes on the next rip. The name-keyed hash ignores both
    // the collection name and the PathID.
    [Fact]
    public void NameKeyedGuidIsIndependentOfBuildIdentity()
    {
        var oldBuild = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("arc", "Menace/lit_highlight", 48);
        var newBuild = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("arc", "Menace/lit_highlight", 48);

        Assert.Equal(oldBuild, newBuild);
    }

    [Fact]
    public void NameKeyedGuidDiffersByNamespace()
    {
        var arc = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("arc", "Menace/lit_highlight", 48);
        var sniper = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("sniper", "Menace/lit_highlight", 48);

        Assert.NotEqual(arc, sniper);
    }

    [Fact]
    public void NameKeyedGuidDiffersByName()
    {
        var highlight = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("arc", "Menace/lit_highlight", 48);
        var character = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("arc", "Menace/character", 48);

        Assert.NotEqual(highlight, character);
    }

    // The "name:" domain prefix keeps the two hash families apart even when a collection
    // name happens to spell out the same bytes as a salted asset name.
    [Fact]
    public void NameKeyedGuidDoesNotCollideWithPathIdGuidForSameString()
    {
        var nameKeyed = AssetExportCollection<IUnityObjectBase>.ComputeNameStableGuid("arc", "resources.assets", 48);
        var pathKeyed = AssetExportCollection<IUnityObjectBase>.ComputeStableGuid("arc/resources.assets", 0, 48);

        Assert.NotEqual(nameKeyed, pathKeyed);
    }
}
