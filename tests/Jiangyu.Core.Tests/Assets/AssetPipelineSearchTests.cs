using System.Text.Json;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;
using Jiangyu.Core.Tests.Helpers;

namespace Jiangyu.Core.Tests.Assets;

public class AssetPipelineSearchTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly AssetPipelineService _service;

    public AssetPipelineSearchTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_cacheDir);

        // Write a fixture index
        var index = new AssetIndex
        {
            Assets =
            [
                new AssetEntry { Name = "Soldier_Body", CanonicalPath = "level0/Mesh/Soldier_Body--100", ClassName = "Mesh", ClassId = 43, PathId = 100, Collection = "level0" },
                new AssetEntry { Name = "Soldier_Body", CanonicalPath = "level0/GameObject/Soldier_Body--101", ClassName = "GameObject", ClassId = 1, PathId = 101, Collection = "level0" },
                new AssetEntry { Name = "Soldier_Head", CanonicalPath = "level0/Mesh/Soldier_Head--200", ClassName = "Mesh", ClassId = 43, PathId = 200, Collection = "level0" },
                new AssetEntry { Name = "Alien_Body", CanonicalPath = "sharedassets1/Mesh/Alien_Body--300", ClassName = "Mesh", ClassId = 43, PathId = 300, Collection = "sharedassets1" },
                new AssetEntry { Name = "Tank_Hull", CanonicalPath = "sharedassets2/GameObject/Tank_Hull--400", ClassName = "GameObject", ClassId = 1, PathId = 400, Collection = "sharedassets2" },
                new AssetEntry { Name = "Rifle_Model", CanonicalPath = "sharedassets2/Mesh/Rifle_Model--500", ClassName = "Mesh", ClassId = 43, PathId = 500, Collection = "sharedassets2" },
            ],
        };

        File.WriteAllText(
            Path.Combine(_cacheDir, "asset-index.json"),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

        _service = new AssetPipelineService("/fake/game/path", _cacheDir, new NullProgressSink(), new NullLogSink());
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    [Fact]
    public void LoadIndex_ReturnsFixtureData()
    {
        var index = _service.LoadIndex();

        Assert.NotNull(index?.Assets);
        Assert.Equal(6, index.Assets.Count);
    }

    [Fact]
    public void LoadIndex_NoCacheFile_ReturnsNull()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"jiangyu-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var service = new AssetPipelineService("/fake", emptyDir, new NullProgressSink(), new NullLogSink());
            Assert.Null(service.LoadIndex());
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public void Search_NoFilter_ReturnsAll()
    {
        var results = _service.Search();

        Assert.Equal(6, results.Count);
    }

    [Fact]
    public void Search_ByName_MatchesCaseInsensitive()
    {
        var results = _service.Search(query: "soldier");

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Contains("Soldier", r.Name));
    }

    [Fact]
    public void Search_ByCanonicalPathSegment_MatchesCaseInsensitive()
    {
        var results = _service.Search(query: "sharedassets2/mesh");

        Assert.Single(results);
        Assert.Equal("Rifle_Model", results[0].Name);
        Assert.Equal("sharedassets2/Mesh/Rifle_Model--500", results[0].CanonicalPath);
    }

    [Fact]
    public void Search_ByTypeFilter_FiltersByClassName()
    {
        var results = _service.Search(typeFilter: "Mesh");

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal("Mesh", r.ClassName));
    }

    [Fact]
    public void Search_CombinedQueryAndType_IntersectsFilters()
    {
        var results = _service.Search(query: "soldier", typeFilter: "Mesh");

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Contains("Soldier", r.Name);
            Assert.Equal("Mesh", r.ClassName);
        });
    }

    [Fact]
    public void Search_Limit_TruncatesResults()
    {
        var results = _service.Search(limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_NoMatches_ReturnsEmpty()
    {
        var results = _service.Search(query: "nonexistent_asset");

        Assert.Empty(results);
    }

    [Fact]
    public void ResolveAsset_MatchesByNameAndClass()
    {
        var result = _service.ResolveAsset("Soldier_Body", "Mesh");

        Assert.NotNull(result);
        Assert.Equal("Soldier_Body", result.Name);
        Assert.Equal("Mesh", result.ClassName);
        Assert.Equal(100, result.PathId);
        Assert.Equal("level0", result.Collection);
    }

    [Fact]
    public void ResolveAsset_MatchesFirstOfMultipleClasses()
    {
        var result = _service.ResolveAsset("Soldier_Body", "Mesh", "GameObject");

        Assert.NotNull(result);
        Assert.Equal("Soldier_Body", result.Name);
    }

    [Fact]
    public void ResolveAsset_CaseInsensitive()
    {
        var result = _service.ResolveAsset("soldier_body", "mesh");

        Assert.NotNull(result);
        Assert.Equal("Soldier_Body", result.Name);
    }

    [Fact]
    public void ResolveAsset_NoMatch_ReturnsNull()
    {
        var result = _service.ResolveAsset("Nonexistent", "Mesh");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveAsset_WrongClass_ReturnsNull()
    {
        var result = _service.ResolveAsset("Soldier_Body", "Material");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveAsset_PreservesStableIdentity()
    {
        var mesh = _service.ResolveAsset("Soldier_Body", "Mesh");
        var go = _service.ResolveAsset("Soldier_Body", "GameObject");

        Assert.NotNull(mesh);
        Assert.NotNull(go);
        Assert.Equal(100, mesh.PathId);
        Assert.Equal("level0", mesh.Collection);
        Assert.Equal("level0/Mesh/Soldier_Body--100", mesh.CanonicalPath);
        Assert.Equal(101, go.PathId);
        Assert.Equal("level0", go.Collection);
        Assert.Equal("level0/GameObject/Soldier_Body--101", go.CanonicalPath);
    }
}
