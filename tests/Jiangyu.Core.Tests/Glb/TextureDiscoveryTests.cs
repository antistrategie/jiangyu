using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Glb;

public class EnumerateSidecarTexturePathsTests : IDisposable
{
    private readonly string _tempDir;

    public EnumerateSidecarTexturePathsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void FindsTexturesInSidecarDir()
    {
        // Setup: source file + textures/ sibling directory
        var sourceFile = Path.Combine(_tempDir, "model.glb");
        File.WriteAllBytes(sourceFile, []);

        var texturesDir = Path.Combine(_tempDir, "textures");
        Directory.CreateDirectory(texturesDir);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_Normal.png"), []);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_BaseMap.png"), []);
        File.WriteAllBytes(Path.Combine(texturesDir, "unrelated.png"), []);

        var results = GlbMeshBundleCompiler.EnumerateSidecarTexturePaths(sourceFile, "soldier").ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("soldier", Path.GetFileName(r)));
        Assert.DoesNotContain(results, r => r.Contains("unrelated"));
    }

    [Fact]
    public void FindsTexturesInParentTexture2DDir()
    {
        // Setup: source in subdir, Texture2D as sibling of parent
        var modelDir = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(modelDir);
        var sourceFile = Path.Combine(modelDir, "soldier.glb");
        File.WriteAllBytes(sourceFile, []);

        var texture2dDir = Path.Combine(_tempDir, "Texture2D");
        Directory.CreateDirectory(texture2dDir);
        File.WriteAllBytes(Path.Combine(texture2dDir, "soldier_MaskMap.png"), []);

        var results = GlbMeshBundleCompiler.EnumerateSidecarTexturePaths(sourceFile, "soldier").ToList();

        Assert.Single(results);
        Assert.Contains("soldier_MaskMap.png", results[0]);
    }

    [Fact]
    public void MatchesMultipleImageFormats()
    {
        var sourceFile = Path.Combine(_tempDir, "model.glb");
        File.WriteAllBytes(sourceFile, []);

        var texturesDir = Path.Combine(_tempDir, "textures");
        Directory.CreateDirectory(texturesDir);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_Normal.png"), []);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_BaseMap.jpg"), []);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_data.txt"), []);

        var results = GlbMeshBundleCompiler.EnumerateSidecarTexturePaths(sourceFile, "soldier").ToList();

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.EndsWith(".txt"));
    }

    [Fact]
    public void NoTexturesDir_ReturnsEmpty()
    {
        var sourceFile = Path.Combine(_tempDir, "model.glb");
        File.WriteAllBytes(sourceFile, []);

        var results = GlbMeshBundleCompiler.EnumerateSidecarTexturePaths(sourceFile, "soldier").ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void ResultsAreSorted()
    {
        var sourceFile = Path.Combine(_tempDir, "model.glb");
        File.WriteAllBytes(sourceFile, []);

        var texturesDir = Path.Combine(_tempDir, "textures");
        Directory.CreateDirectory(texturesDir);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_Normal.png"), []);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_BaseMap.png"), []);
        File.WriteAllBytes(Path.Combine(texturesDir, "soldier_MaskMap.png"), []);

        var results = GlbMeshBundleCompiler.EnumerateSidecarTexturePaths(sourceFile, "soldier").ToList();

        Assert.Equal(3, results.Count);
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(string.Compare(results[i - 1], results[i], StringComparison.OrdinalIgnoreCase) <= 0);
        }
    }
}
