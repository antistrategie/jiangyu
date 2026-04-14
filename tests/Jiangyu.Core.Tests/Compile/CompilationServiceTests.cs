using Jiangyu.Core.Compile;
using Jiangyu.Core.Unity;
using Jiangyu.Core.Tests.Helpers;
using AssetRipper.Primitives;

namespace Jiangyu.Core.Tests.Compile;

public class ParseAssetReferenceTests
{
    private const string ProjectDir = "/tmp/test-project";

    [Fact]
    public void WithFragment_ExtractsMeshName()
    {
        var result = CompilationService.ParseAssetReference("models/soldier.glb#body", ProjectDir);

        Assert.Equal(Path.Combine(ProjectDir, "models", "soldier.glb"), result.FilePath);
        Assert.Equal("body", result.MeshName);
        Assert.True(result.HasExplicitMeshName);
    }

    [Fact]
    public void WithoutFragment_UsesFileNameAsMeshName()
    {
        var result = CompilationService.ParseAssetReference("models/soldier.glb", ProjectDir);

        Assert.Equal(Path.Combine(ProjectDir, "models", "soldier.glb"), result.FilePath);
        Assert.Equal("soldier", result.MeshName);
        Assert.False(result.HasExplicitMeshName);
    }

    [Fact]
    public void AbsolutePath_PreservedWithFragment()
    {
        var result = CompilationService.ParseAssetReference("/absolute/path.gltf#mesh", ProjectDir);

        Assert.Equal("/absolute/path.gltf", result.FilePath);
        Assert.Equal("mesh", result.MeshName);
        Assert.True(result.HasExplicitMeshName);
    }

    [Fact]
    public void AbsolutePath_PreservedWithoutFragment()
    {
        var result = CompilationService.ParseAssetReference("/absolute/path.gltf", ProjectDir);

        Assert.Equal("/absolute/path.gltf", result.FilePath);
        Assert.Equal("path", result.MeshName);
        Assert.False(result.HasExplicitMeshName);
    }

    [Fact]
    public void BareFilename_ResolvesRelativeToProject()
    {
        var result = CompilationService.ParseAssetReference("tank.fbx", ProjectDir);

        Assert.Equal(Path.Combine(ProjectDir, "tank.fbx"), result.FilePath);
        Assert.Equal("tank", result.MeshName);
        Assert.False(result.HasExplicitMeshName);
    }

    [Fact]
    public void EmptyFragment_ReturnsEmptyMeshName()
    {
        var result = CompilationService.ParseAssetReference("models/file.glb#", ProjectDir);

        Assert.Equal("", result.MeshName);
        Assert.True(result.HasExplicitMeshName);
    }
}

public class IsGlbPathTests
{
    [Theory]
    [InlineData("file.glb", true)]
    [InlineData("file.gltf", true)]
    [InlineData("file.GLB", true)]
    [InlineData("file.GLTF", true)]
    [InlineData("file.Glb", true)]
    [InlineData("file.fbx", false)]
    [InlineData("file.obj", false)]
    [InlineData("file", false)]
    [InlineData("file.glb.bak", false)]
    public void DetectsGlbAndGltfExtensions(string path, bool expected)
    {
        Assert.Equal(expected, CompilationService.IsGlbPath(path));
    }
}

public class CollectAssetFilesTests
{
    [Fact]
    public void NonexistentDirectory_ReturnsEmpty()
    {
        var result = CompilationService.CollectAssetFiles("/nonexistent/path/that/does/not/exist");
        Assert.Empty(result);
    }

    [Fact]
    public void MatchesMultiplePatterns()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "a.glb"), []);
            File.WriteAllBytes(Path.Combine(dir, "b.gltf"), []);
            File.WriteAllBytes(Path.Combine(dir, "c.txt"), []);

            var result = CompilationService.CollectAssetFiles(dir, "*.glb", "*.gltf");

            Assert.Equal(2, result.Length);
            Assert.Contains(result, f => f.EndsWith("a.glb"));
            Assert.Contains(result, f => f.EndsWith("b.gltf"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResultsAreSorted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "c.glb"), []);
            File.WriteAllBytes(Path.Combine(dir, "a.glb"), []);
            File.WriteAllBytes(Path.Combine(dir, "b.glb"), []);

            var result = CompilationService.CollectAssetFiles(dir, "*.glb");

            Assert.Equal(3, result.Length);
            Assert.True(string.Compare(result[0], result[1], StringComparison.Ordinal) < 0);
            Assert.True(string.Compare(result[1], result[2], StringComparison.Ordinal) < 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class UnityVersionValidationServiceTests
{
    [Theory]
    [InlineData("/opt/Unity/Hub/Editor/6000.0.63f1/Editor/Unity", "6000.0.63f1")]
    [InlineData("C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.63f1\\Editor\\Unity.exe", "6000.0.63f1")]
    [InlineData("Unity 6000.0.63f1", "6000.0.63f1")]
    public void TryParseUnityVersionFromText_FindsUnityVersion(string text, string expected)
    {
        var success = UnityVersionValidationService.TryParseUnityVersionFromText(text, out var version);

        Assert.True(success);
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("/opt/Unity/Hub/Editor/current/Editor/Unity")]
    [InlineData("not a unity version")]
    [InlineData("6000")]
    public void TryParseUnityVersionFromText_RejectsNonVersionText(string text)
    {
        var success = UnityVersionValidationService.TryParseUnityVersionFromText(text, out _);

        Assert.False(success);
    }

    [Fact]
    public async Task ValidateAsync_FailsWhenEditorDoesNotMatchGame()
    {
        var service = new UnityVersionValidationService(
            new NullLogSink(),
            _ => ParseVersion("6000.0.63f1"),
            _ => Task.FromResult<UnityVersion?>(ParseVersion("6000.0.55f1")));

        var result = await service.ValidateAsync("/fake/Menace_Data", "/fake/Unity");

        Assert.False(result.Success);
        Assert.Equal("6000.0.63f1", result.GameVersion?.ToString());
        Assert.Equal("6000.0.55f1", result.EditorVersion?.ToString());
        Assert.Contains("does not match MENACE", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_SucceedsWhenEditorMatchesGame()
    {
        var service = new UnityVersionValidationService(
            new NullLogSink(),
            _ => ParseVersion("6000.0.63f1"),
            _ => Task.FromResult<UnityVersion?>(ParseVersion("6000.0.63f1")));

        var result = await service.ValidateAsync("/fake/Menace_Data", "/fake/Unity");

        Assert.True(result.Success);
        Assert.Equal("6000.0.63f1", result.GameVersion?.ToString());
        Assert.Equal("6000.0.63f1", result.EditorVersion?.ToString());
        Assert.Null(result.ErrorMessage);
    }

    private static UnityVersion ParseVersion(string value) => UnityVersion.Parse(value);
}
