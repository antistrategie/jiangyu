using System.Security.Cryptography;
using System.Text.Json;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;
using Jiangyu.Core.Tests.Helpers;

namespace Jiangyu.Core.Tests.Assets;

public sealed class TemplateIndexServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _rootDir;
    private readonly string _gameRoot;
    private readonly string _gameDataPath;
    private readonly string _cacheDir;
    private readonly TemplateIndexService _service;

    public TemplateIndexServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"jiangyu-template-test-{Guid.NewGuid()}");
        _gameRoot = Path.Combine(_rootDir, "Menace");
        _gameDataPath = Path.Combine(_gameRoot, "Menace_Data");
        _cacheDir = Path.Combine(_rootDir, "cache");

        Directory.CreateDirectory(_gameDataPath);
        Directory.CreateDirectory(_cacheDir);

        File.WriteAllBytes(Path.Combine(_gameRoot, "GameAssembly.so"), [1, 2, 3, 4]);

        _service = new TemplateIndexService(_gameDataPath, _cacheDir, new NullProgressSink(), new NullLogSink());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    [Fact]
    public void LoadIndex_ReturnsFixtureData()
    {
        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes =
            [
                new TemplateTypeEntry { ClassName = "EntityTemplate", Count = 1, ClassifiedVia = "suffix" },
            ],
            Instances =
            [
                new TemplateInstanceEntry
                {
                    Name = "bunker",
                    ClassName = "EntityTemplate",
                    Identity = new TemplateIdentity { Collection = "resources.assets", PathId = 123 },
                },
            ],
        };

        File.WriteAllText(
            Path.Combine(_cacheDir, "template-index.json"),
            JsonSerializer.Serialize(index, JsonOptions));

        TemplateIndex? loaded = _service.LoadIndex();

        Assert.NotNull(loaded);
        Assert.Single(loaded.Instances);
        Assert.Equal("EntityTemplate", loaded.TemplateTypes.Single().ClassName);
    }

    [Fact]
    public void IsIndexCurrent_ReturnsTrue_WhenManifestMatchesHashAndRule()
    {
        WriteIndex();
        WriteManifest(new TemplateIndexManifest
        {
            GameAssemblyHash = ComputeGameAssemblyHash(),
            IndexedAt = DateTimeOffset.UtcNow,
            GameDataPath = _gameDataPath,
            RuleVersion = TemplateClassifier.RuleVersion,
            RuleDescription = TemplateClassifier.RuleDescription,
            TemplateTypeCount = 2,
            InstanceCount = 5,
        });

        Assert.True(_service.IsIndexCurrent());
    }

    [Fact]
    public void IsIndexCurrent_ReturnsFalse_WhenRuleVersionDiffers()
    {
        WriteIndex();
        WriteManifest(new TemplateIndexManifest
        {
            GameAssemblyHash = ComputeGameAssemblyHash(),
            IndexedAt = DateTimeOffset.UtcNow,
            GameDataPath = _gameDataPath,
            RuleVersion = "v0",
            RuleDescription = TemplateClassifier.RuleDescription,
            TemplateTypeCount = 2,
            InstanceCount = 5,
        });

        Assert.False(_service.IsIndexCurrent());
    }

    private void WriteManifest(TemplateIndexManifest manifest)
    {
        File.WriteAllText(
            Path.Combine(_cacheDir, "template-index-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private void WriteIndex()
    {
        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes = [],
            Instances = [],
        };

        File.WriteAllText(
            Path.Combine(_cacheDir, "template-index.json"),
            JsonSerializer.Serialize(index, JsonOptions));
    }

    private string ComputeGameAssemblyHash()
    {
        using var stream = File.OpenRead(Path.Combine(_gameRoot, "GameAssembly.so"));
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
