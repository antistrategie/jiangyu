using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Unity;
using Xunit;

namespace Jiangyu.Core.Tests.Unity;

public sealed class UnityProjectScaffolderTests : IDisposable
{
    private readonly string _tempRoot;

    public UnityProjectScaffolderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"jiangyu-scaffolder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void FreshInit_CreatesExpectedTree()
    {
        var scaffolder = new UnityProjectScaffolder(new NullLog());

        scaffolder.Init(_tempRoot);

        var unityDir = Path.Combine(_tempRoot, "unity");
        Assert.True(Directory.Exists(unityDir));
        Assert.True(File.Exists(Path.Combine(unityDir, "Assets", "Jiangyu", "Editor", "BuildBundles.cs")));
        Assert.True(File.Exists(Path.Combine(unityDir, "Assets", "Jiangyu", "Editor", "ImportedPrefabPostProcessor.cs")));
        Assert.True(File.Exists(Path.Combine(unityDir, "Assets", "Jiangyu", "Editor", "BakeHumanoid.cs")));
        Assert.True(File.Exists(Path.Combine(unityDir, "Assets", "Jiangyu", "README.md")));
        Assert.True(File.Exists(Path.Combine(unityDir, "Assets", "Prefabs", ".gitkeep")));
        Assert.True(File.Exists(Path.Combine(unityDir, "Packages", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(unityDir, ".gitignore")));
    }

    [Fact]
    public void Rerun_OverwritesJiangyuOwnedFiles()
    {
        var scaffolder = new UnityProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot);

        var buildBundlesPath = Path.Combine(_tempRoot, "unity", "Assets", "Jiangyu", "Editor", "BuildBundles.cs");
        File.WriteAllText(buildBundlesPath, "// modder hacked this");

        scaffolder.Init(_tempRoot);

        var content = File.ReadAllText(buildBundlesPath);
        Assert.Contains("Jiangyu.Mod", content);
        Assert.DoesNotContain("modder hacked this", content);
    }

    [Fact]
    public void Rerun_PreservesModderPrefabs()
    {
        var scaffolder = new UnityProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot);

        var modderPrefab = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs", "MyPrefab.prefab");
        File.WriteAllText(modderPrefab, "fake prefab content");

        scaffolder.Init(_tempRoot);

        Assert.True(File.Exists(modderPrefab));
        Assert.Equal("fake prefab content", File.ReadAllText(modderPrefab));
    }

    [Fact]
    public void Rerun_PreservesModderPackageManifest()
    {
        var scaffolder = new UnityProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot);

        var manifestPath = Path.Combine(_tempRoot, "unity", "Packages", "manifest.json");
        var customManifest = "{ \"dependencies\": { \"com.unity.fake\": \"1.0.0\" } }";
        File.WriteAllText(manifestPath, customManifest);

        var result = scaffolder.Init(_tempRoot);

        Assert.Equal(customManifest, File.ReadAllText(manifestPath));
        Assert.Contains(manifestPath, result.PreservedFiles);
    }

    [Fact]
    public void Rerun_PreservesUnknownContent()
    {
        var scaffolder = new UnityProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot);

        var projectSettingsDir = Path.Combine(_tempRoot, "unity", "ProjectSettings");
        Directory.CreateDirectory(projectSettingsDir);
        var unityFile = Path.Combine(projectSettingsDir, "ProjectVersion.txt");
        File.WriteAllText(unityFile, "m_EditorVersion: 6000.0.72f1");

        var modderScript = Path.Combine(_tempRoot, "unity", "Assets", "Editor", "MyTool.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(modderScript)!);
        File.WriteAllText(modderScript, "// modder's helper");

        scaffolder.Init(_tempRoot);

        Assert.Equal("m_EditorVersion: 6000.0.72f1", File.ReadAllText(unityFile));
        Assert.Equal("// modder's helper", File.ReadAllText(modderScript));
    }

    [Fact]
    public void BakeHumanoid_TemplateContractsAreStable()
    {
        // BakeHumanoid is invoked by external pipelines via fixed CLI flags
        // and a fixed batchmode method name. Lock the contract here so a
        // future edit that drops a flag or renames the method fails loud.
        var scaffolder = new UnityProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot);

        var content = File.ReadAllText(
            Path.Combine(_tempRoot, "unity", "Assets", "Jiangyu", "Editor", "BakeHumanoid.cs"));

        // Namespace and class identity.
        Assert.Contains("namespace Jiangyu.Mod", content);
        Assert.Contains("class BakeHumanoid", content);
        Assert.Contains("public static void BakeBatch()", content);

        // Batchmode CLI flags consumed by external invokers.
        Assert.Contains("\"-gltfFolder\"", content);
        Assert.Contains("\"-referencePrefab\"", content);
        Assert.Contains("\"-outputDir\"", content);
        Assert.Contains("\"-outputName\"", content);
        // Dropped in this generation: -meshBasename (auto-detected now).
        Assert.DoesNotContain("\"-meshBasename\"", content);

        // T-pose requirement must stay documented; without it modders ship
        // broken Mecanim retargets.
        Assert.Contains("T-pose", content);

        // The "main.prefab" convention is the KDL contract surface (asset=
        // "<name>/main"); locking it here so a future rename doesn't
        // silently break every existing mod's KDL.
        Assert.Contains("main.prefab", content);

        // MENACE humanoid bone naming convention is the public contract.
        Assert.Contains("Hips", content);
        Assert.Contains("UpperArm_L", content);
        Assert.Contains("Foot_R", content);
    }

    [Fact]
    public void FreshInit_GitignoreCoversUnityArtifacts()
    {
        var scaffolder = new UnityProjectScaffolder(new NullLog());
        scaffolder.Init(_tempRoot);

        var gitignore = File.ReadAllText(Path.Combine(_tempRoot, "unity", ".gitignore"));
        Assert.Contains("Library/", gitignore);
        Assert.Contains("Temp/", gitignore);
        Assert.Contains("Logs/", gitignore);
    }

    private sealed class NullLog : ILogSink
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
