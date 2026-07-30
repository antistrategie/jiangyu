using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Tests.Unity;

/// <summary>
/// The Unity-side build scripts only compile inside a Unity Editor, so these tests pin
/// their load-bearing shapes at the source level from the scaffolded template output.
/// </summary>
public sealed class UnityBuildScriptInvariantTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _editorDir;

    public UnityBuildScriptInvariantTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"jiangyu-build-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        new UnityProjectScaffolder(NullLogSink.Instance).Init(_tempRoot);
        _editorDir = Path.Combine(_tempRoot, "unity", "Assets", "Jiangyu", "Editor");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void PrefabPass_BuildsIncrementallyWithOneForcedRetry()
    {
        var source = File.ReadAllText(Path.Combine(_editorDir, "BuildBundles.cs"));

        // The first build call trusts Unity's per-bundle hashing so an unchanged prefab's
        // bundle is not rebuilt. The stale-manifest hazard (Unity skipping a bundle yet
        // reporting success) is covered by verifying output and retrying forced once.
        var firstBuild = source.IndexOf("BuildPipeline.BuildAssetBundles", StringComparison.Ordinal);
        Assert.True(firstBuild >= 0);
        var firstCallArgs = source[firstBuild..source.IndexOf(';', firstBuild)];
        Assert.DoesNotContain("ForceRebuildAssetBundle", firstCallArgs);

        Assert.Contains("ForceRebuildAssetBundle", source);
        Assert.Contains("Jiangyu BuildBundles (incremental)", source);
        Assert.Contains("Jiangyu BuildBundles (forced)", source);
    }

    [Fact]
    public void ReplacementBundle_AssignsMembershipOnlyThroughTheExplicitBuildMap()
    {
        var source = File.ReadAllText(Path.Combine(_editorDir, "BuildMeshReplacementBundle.cs"));

        // The prefab pass builds every persistent assetBundleName assignment in the
        // project, so a staged or generated replacement asset carrying one would fold the
        // whole replacement set into that pass. Membership must come only from the
        // AssetBundleBuild map handed to BuildAssetBundles.
        Assert.DoesNotContain("assetBundleName = bundleName", source);
        Assert.Contains("assetBundleName = kvp.Key", source);
        Assert.Contains("importer.assetBundleName = string.Empty", source);
    }

    [Fact]
    public void ReplacementBundle_BuildsIncrementallyWithOneForcedRetry()
    {
        var source = File.ReadAllText(Path.Combine(_editorDir, "BuildMeshReplacementBundle.cs"));

        // Splitting the replacement output only pays when an unchanged group's bundle is
        // not rebuilt, so the first build must trust Unity's per-bundle hashing and the
        // forced rebuild must exist only as the verified-gap recovery.
        var firstBuild = source.IndexOf("BuildPipeline.BuildAssetBundles", StringComparison.Ordinal);
        Assert.True(firstBuild >= 0);
        var firstCallArgs = source[firstBuild..source.IndexOf(';', firstBuild)];
        Assert.DoesNotContain("ForceRebuildAssetBundle", firstCallArgs);
        Assert.Contains("[Jiangyu] (incremental)", source);
        Assert.Contains("[Jiangyu] (forced)", source);
    }

    [Fact]
    public void PrefabPass_PrunesStaleBundlesInsteadOfRelyingOnAWipe()
    {
        var source = File.ReadAllText(Path.Combine(_editorDir, "BuildBundles.cs"));

        // The compile pipeline no longer wipes unity_build/ before the prefab pass (the
        // cached bundles and manifests are the incremental state), so the pass itself
        // must remove bundles whose prefab, UXML, or icon no longer exists.
        Assert.Contains("PruneStaleBundles(outputDir, bundleNames)", source);
    }

    [Fact]
    public void ReplacementBundle_NeverWipesTheGeneratedTree()
    {
        var source = File.ReadAllText(Path.Combine(_editorDir, "BuildMeshReplacementBundle.cs"));

        // A wholesale wipe of Generated/ regenerates every asset's GUID, which makes
        // every planned bundle hash as changed and rebuild. Stale assets go through
        // per-asset DeleteAsset against the baked state instead.
        Assert.DoesNotContain("Directory.Delete(GeneratedDir", source);
        Assert.Contains("AssetDatabase.DeleteAsset", source);
        Assert.Contains("generated_baked", source);
    }

    [Fact]
    public void ReplacementBundle_DoesNotForceReimportSettledSprites()
    {
        var source = File.ReadAllText(Path.Combine(_editorDir, "BuildMeshReplacementBundle.cs"));

        // Staged sprites persist across builds with their metas, so an unconditional
        // SaveAndReimport would re-import every one of them on every build. The importer
        // is only reconfigured when its saved settings differ.
        var reimport = source.IndexOf("importer.SaveAndReimport()", StringComparison.Ordinal);
        Assert.True(reimport >= 0);
        var settingsCheck = source.IndexOf("importer.textureType != TextureImporterType.Sprite", StringComparison.Ordinal);
        Assert.True(settingsCheck >= 0 && settingsCheck < reimport);
    }
}
