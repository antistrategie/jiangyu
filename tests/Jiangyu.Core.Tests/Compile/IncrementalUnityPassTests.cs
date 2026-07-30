using Jiangyu.Core.Compile;

namespace Jiangyu.Core.Tests.Compile;

/// <summary>
/// The asset section of a compile keeps two independently fingerprinted Unity halves
/// (the raw-GLB replacement bundle and the addition prefab bundles). These tests pin
/// the plan chosen for each staleness combination and the independence of the three
/// fingerprints that feed it.
/// </summary>
public sealed class IncrementalUnityPassTests : IDisposable
{
    private readonly string _projectDir;
    private string UnityDir => Path.Combine(_projectDir, "unity");

    public IncrementalUnityPassTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"jiangyu-unity-pass-{Guid.NewGuid():N}");
        Write("assets/additions/audio/clip.wav", "audio");
        Write("unity/Assets/Jiangyu/Editor/BuildBundles.cs", "editor script");
        Write("unity/Assets/Prefabs/doll.prefab", "prefab");
        Write("unity/Assets/Authored/doll/model.gltf", "model");
        Write("unity/ProjectSettings/ProjectSettings.asset", "settings");
        Write("unity/Packages/manifest.json", "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_projectDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (string Project, string Prefabs, string Assets) Fingerprints(string bundleName = "mymod")
    {
        var project = CompilationService.UnityProjectFingerprint(UnityDir, "1.0");
        return (
            project,
            CompilationService.PrefabInputsFingerprint(UnityDir, project),
            CompilationService.AssetInputsFingerprint(_projectDir, project, bundleName));
    }

    [Theory]
    [InlineData(false, true, false, nameof(CompilationService.UnityAssetPassPlan.ReuseCachedBundles))]
    [InlineData(false, false, false, nameof(CompilationService.UnityAssetPassPlan.RunRawGlb))]
    [InlineData(true, true, true, nameof(CompilationService.UnityAssetPassPlan.ReuseCachedBundles))]
    [InlineData(true, true, false, nameof(CompilationService.UnityAssetPassPlan.RebuildPrefabsOnly))]
    [InlineData(true, false, true, nameof(CompilationService.UnityAssetPassPlan.RunRawGlb))]
    [InlineData(true, false, false, nameof(CompilationService.UnityAssetPassPlan.RunRawGlbWithPrefabs))]
    public void PlanCoversEveryStalenessCombination(
        bool combinedUnityPass, bool reuseRawGlb, bool reuseCombinedPrefabs, string expected)
    {
        var plan = CompilationService.PlanUnityAssetPass(combinedUnityPass, reuseRawGlb, reuseCombinedPrefabs);
        Assert.Equal(expected, plan.ToString());
    }

    [Fact]
    public void PrefabEditLeavesAssetFingerprintCurrent()
    {
        var before = Fingerprints();

        Write("unity/Assets/Prefabs/doll.prefab", "prefab edited");
        var after = Fingerprints();

        Assert.Equal(before.Project, after.Project);
        Assert.NotEqual(before.Prefabs, after.Prefabs);
        Assert.Equal(before.Assets, after.Assets);
    }

    [Fact]
    public void AuthoredModelEditLeavesAssetFingerprintCurrent()
    {
        // A prefab bundle pulls in what the prefab references, so authored sources under
        // unity/Assets count as prefab inputs even though no bundle is rooted at them.
        var before = Fingerprints();

        Write("unity/Assets/Authored/doll/model.gltf", "rebaked");
        var after = Fingerprints();

        Assert.NotEqual(before.Prefabs, after.Prefabs);
        Assert.Equal(before.Assets, after.Assets);
    }

    [Fact]
    public void AssetEditLeavesPrefabFingerprintCurrent()
    {
        var before = Fingerprints();

        Write("assets/additions/audio/clip.wav", "audio edited");
        var after = Fingerprints();

        Assert.Equal(before.Project, after.Project);
        Assert.Equal(before.Prefabs, after.Prefabs);
        Assert.NotEqual(before.Assets, after.Assets);
    }

    [Fact]
    public void EditorScriptEditInvalidatesBothHalves()
    {
        var before = Fingerprints();

        Write("unity/Assets/Jiangyu/Editor/BuildBundles.cs", "editor script edited");
        var after = Fingerprints();

        Assert.NotEqual(before.Project, after.Project);
        Assert.NotEqual(before.Prefabs, after.Prefabs);
        Assert.NotEqual(before.Assets, after.Assets);
    }

    [Fact]
    public void RegeneratedTreesInvalidateNothing()
    {
        var before = Fingerprints();

        Write("unity/Assets/Jiangyu/Staging/MeshReplacement/Audio/staged.wav", "staged copy");
        Write("unity/Assets/Library/ArtifactDB", "unity cache");
        Write("unity/Assets/Temp/scratch", "scratch");
        var after = Fingerprints();

        Assert.Equal(before.Project, after.Project);
        Assert.Equal(before.Prefabs, after.Prefabs);
        Assert.Equal(before.Assets, after.Assets);
    }

    [Fact]
    public void GameVersionChangeInvalidatesBothHalves()
    {
        var before = Fingerprints();

        var project = CompilationService.UnityProjectFingerprint(UnityDir, "2.0");
        var prefabs = CompilationService.PrefabInputsFingerprint(UnityDir, project);
        var assets = CompilationService.AssetInputsFingerprint(_projectDir, project, "mymod");

        Assert.NotEqual(before.Project, project);
        Assert.NotEqual(before.Prefabs, prefabs);
        Assert.NotEqual(before.Assets, assets);
    }

    [Fact]
    public void ModRenameInvalidatesTheAssetHalf()
    {
        // The bundle name prefixes every planned replacement bundle file, so a rename
        // must rebuild rather than reuse bundles recorded under the old name.
        var before = Fingerprints(bundleName: "alpha");
        var after = Fingerprints(bundleName: "beta");

        Assert.Equal(before.Prefabs, after.Prefabs);
        Assert.NotEqual(before.Assets, after.Assets);
    }

    [Fact]
    public void ModderEditorScriptInvalidatesBothHalves()
    {
        // A C# file anywhere under unity/Assets compiles into every batchmode session
        // and can change how both passes import and bake.
        var before = Fingerprints();

        Write("unity/Assets/Editor/TexturePreprocessor.cs", "class TexturePreprocessor { }");
        var after = Fingerprints();

        Assert.NotEqual(before.Project, after.Project);
        Assert.NotEqual(before.Prefabs, after.Prefabs);
        Assert.NotEqual(before.Assets, after.Assets);
    }
}
