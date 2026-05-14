using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Compile;
using Jiangyu.Core.Models;
using Xunit;

namespace Jiangyu.Core.Tests.Compile;

public sealed class AdditionPrefabStagingTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _outputDir;
    private readonly RecordingLog _log;

    public AdditionPrefabStagingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"jiangyu-stage-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _outputDir = Path.Combine(_tempRoot, "compiled");
        Directory.CreateDirectory(_outputDir);
        _log = new RecordingLog();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Stage_NoSourceDirs_LeavesManifestUntouched()
    {
        var manifest = NewManifest();

        AdditionPrefabStaging.Stage([], _outputDir, manifest, _log);

        Assert.Null(manifest.AdditionPrefabs);
        Assert.Empty(Directory.GetFiles(_outputDir));
    }

    [Fact]
    public void Stage_MissingSourceDir_Skipped()
    {
        var manifest = NewManifest();
        var missing = Path.Combine(_tempRoot, "does-not-exist");

        AdditionPrefabStaging.Stage([missing], _outputDir, manifest, _log);

        Assert.Null(manifest.AdditionPrefabs);
    }

    [Fact]
    public void Stage_SingleSource_CopiesAndRecords()
    {
        var manifest = NewManifest();
        var sourceDir = MakeSourceDir("primary", ("voymastina_darby", "voymastina contents"));

        AdditionPrefabStaging.Stage([sourceDir], _outputDir, manifest, _log);

        Assert.Equal(new[] { "voymastina_darby" }, manifest.AdditionPrefabs);
        var staged = Path.Combine(_outputDir, "voymastina_darby.bundle");
        Assert.True(File.Exists(staged));
        Assert.Equal("voymastina contents", File.ReadAllText(staged));
    }

    [Fact]
    public void Stage_MultipleSources_NameCollision_LaterWins()
    {
        var manifest = NewManifest();
        var escapeHatch = MakeSourceDir("escape", ("widget", "stale hand-shipped"));
        var unityBuild = MakeSourceDir("unity-build", ("widget", "fresh unity-built"));

        AdditionPrefabStaging.Stage([escapeHatch, unityBuild], _outputDir, manifest, _log);

        Assert.Equal(new[] { "widget" }, manifest.AdditionPrefabs);
        var staged = Path.Combine(_outputDir, "widget.bundle");
        Assert.Equal("fresh unity-built", File.ReadAllText(staged));
    }

    [Fact]
    public void Stage_MultipleSources_NoCollision_BothStaged()
    {
        var manifest = NewManifest();
        var escapeHatch = MakeSourceDir("escape", ("alpha", "alpha contents"));
        var unityBuild = MakeSourceDir("unity-build", ("beta", "beta contents"));

        AdditionPrefabStaging.Stage([escapeHatch, unityBuild], _outputDir, manifest, _log);

        Assert.Equal(new[] { "alpha", "beta" }, manifest.AdditionPrefabs!.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.True(File.Exists(Path.Combine(_outputDir, "alpha.bundle")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "beta.bundle")));
    }

    [Fact]
    public void Stage_NamesAreSortedDeterministically()
    {
        var manifest = NewManifest();
        var source = MakeSourceDir("primary",
            ("zulu", "z"),
            ("alpha", "a"),
            ("mike", "m"));

        AdditionPrefabStaging.Stage([source], _outputDir, manifest, _log);

        Assert.Equal(new[] { "alpha", "mike", "zulu" }, manifest.AdditionPrefabs);
    }

    [Fact]
    public void ShouldInvokeUnityForPrefabs_NoUnityDir_False()
    {
        Assert.False(AdditionPrefabStaging.ShouldInvokeUnityForPrefabs(_tempRoot));
    }

    [Fact]
    public void ShouldInvokeUnityForPrefabs_EmptyPrefabsDir_False()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "unity", "Assets", "Prefabs"));

        Assert.False(AdditionPrefabStaging.ShouldInvokeUnityForPrefabs(_tempRoot));
    }

    [Fact]
    public void ShouldInvokeUnityForPrefabs_HasPrefab_True()
    {
        var prefabsDir = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs");
        Directory.CreateDirectory(prefabsDir);
        File.WriteAllText(Path.Combine(prefabsDir, "test.prefab"), "fake prefab");

        Assert.True(AdditionPrefabStaging.ShouldInvokeUnityForPrefabs(_tempRoot));
    }

    [Fact]
    public void ShouldInvokeUnityForPrefabs_OtherFilesOnly_False()
    {
        var prefabsDir = Path.Combine(_tempRoot, "unity", "Assets", "Prefabs");
        Directory.CreateDirectory(prefabsDir);
        File.WriteAllText(Path.Combine(prefabsDir, ".gitkeep"), "");
        File.WriteAllText(Path.Combine(prefabsDir, "README.md"), "");

        Assert.False(AdditionPrefabStaging.ShouldInvokeUnityForPrefabs(_tempRoot));
    }

    private string MakeSourceDir(string name, params (string stem, string contents)[] bundles)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        foreach (var (stem, contents) in bundles)
            File.WriteAllText(Path.Combine(dir, stem + ".bundle"), contents);
        return dir;
    }

    private static ModManifest NewManifest() => new() { Name = "Test" };

    private sealed class RecordingLog : ILogSink
    {
        public readonly List<string> Infos = new();
        public readonly List<string> Warnings = new();
        public readonly List<string> Errors = new();
        public void Info(string message) => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
    }
}
