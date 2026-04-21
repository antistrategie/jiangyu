using System.Text.Json;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Tests.Assets;

public class IndexStatusTests
{
    [Fact]
    public void AssetIndexStatus_WhenMissing_ReturnsMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-index-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var service = new AssetPipelineService("/tmp/fake-game-data", tempDir, NullProgressSink.Instance, NullLogSink.Instance);

            CachedIndexStatus status = service.GetIndexStatus();

            Assert.Equal(CachedIndexState.Missing, status.State);
            Assert.Contains("assets index", status.Reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AssetIndexStatus_WhenManifestHashMatches_ReturnsCurrent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jiangyu-asset-status-{Guid.NewGuid()}");
        var gameDir = Path.Combine(root, "Menace");
        var dataDir = Path.Combine(gameDir, "Menace_Data");
        var cacheDir = Path.Combine(root, "cache");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(Path.Combine(gameDir, "GameAssembly.so"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(cacheDir, "asset-index.json"), """{"assets":[]}""");

        try
        {
            string hash = ComputeHash(Path.Combine(gameDir, "GameAssembly.so"));
            var manifest = new IndexManifest
            {
                GameAssemblyHash = hash,
                IndexedAt = DateTimeOffset.UtcNow,
                GameDataPath = dataDir,
                AssetCount = 0,
            };

            File.WriteAllText(
                Path.Combine(cacheDir, "index-manifest.json"),
                JsonSerializer.Serialize(manifest));

            var service = new AssetPipelineService(dataDir, cacheDir, NullProgressSink.Instance, NullLogSink.Instance);

            CachedIndexStatus status = service.GetIndexStatus();

            Assert.Equal(CachedIndexState.Current, status.State);
            Assert.Null(status.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AssetIndexLoadManifest_WhenPresent_ReturnsManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jiangyu-asset-manifest-{Guid.NewGuid()}");
        var gameDir = Path.Combine(root, "Menace");
        var dataDir = Path.Combine(gameDir, "Menace_Data");
        var cacheDir = Path.Combine(root, "cache");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(Path.Combine(gameDir, "GameAssembly.so"), [1, 2, 3, 4]);

        try
        {
            string hash = ComputeHash(Path.Combine(gameDir, "GameAssembly.so"));
            var manifest = new IndexManifest
            {
                GameAssemblyHash = hash,
                IndexedAt = DateTimeOffset.UtcNow,
                GameDataPath = dataDir,
                AssetCount = 42,
            };

            File.WriteAllText(
                Path.Combine(cacheDir, "index-manifest.json"),
                JsonSerializer.Serialize(manifest));

            var service = new AssetPipelineService(dataDir, cacheDir, NullProgressSink.Instance, NullLogSink.Instance);

            IndexManifest? loaded = service.LoadManifest();

            Assert.NotNull(loaded);
            Assert.Equal(hash, loaded!.GameAssemblyHash);
            Assert.Equal(42, loaded.AssetCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TemplateIndexStatus_WhenRuleVersionDiffers_ReturnsStale()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jiangyu-template-status-{Guid.NewGuid()}");
        var gameDir = Path.Combine(root, "Menace");
        var dataDir = Path.Combine(gameDir, "Menace_Data");
        var cacheDir = Path.Combine(root, "cache");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(Path.Combine(gameDir, "GameAssembly.so"), [1, 2, 3, 4]);

        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes = [],
            Instances = [],
        };
        File.WriteAllText(
            Path.Combine(cacheDir, "template-index.json"),
            JsonSerializer.Serialize(index));

        try
        {
            string hash = ComputeHash(Path.Combine(gameDir, "GameAssembly.so"));
            var manifest = new TemplateIndexManifest
            {
                GameAssemblyHash = hash,
                IndexedAt = DateTimeOffset.UtcNow,
                GameDataPath = dataDir,
                RuleVersion = "legacy-rule",
                RuleDescription = "old classifier",
                TemplateTypeCount = 0,
                InstanceCount = 0,
            };

            File.WriteAllText(
                Path.Combine(cacheDir, "template-index-manifest.json"),
                JsonSerializer.Serialize(manifest));

            var service = new TemplateIndexService(dataDir, cacheDir, NullProgressSink.Instance, NullLogSink.Instance);

            CachedIndexStatus status = service.GetIndexStatus();

            Assert.Equal(CachedIndexState.Stale, status.State);
            Assert.Contains("templates index", status.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}
