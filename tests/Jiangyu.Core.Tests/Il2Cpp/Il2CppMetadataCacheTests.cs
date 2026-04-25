using Jiangyu.Core.Il2Cpp;

namespace Jiangyu.Core.Tests.Il2Cpp;

public sealed class Il2CppMetadataCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jiangyu-il2cpp-cache-{Guid.NewGuid():N}");
    private readonly string _cacheRoot;
    private readonly string _gameAssemblyPath;
    private readonly string _metadataPath;

    public Il2CppMetadataCacheTests()
    {
        _cacheRoot = Path.Combine(_root, "cache");
        _gameAssemblyPath = Path.Combine(_root, "GameAssembly.dll");
        _metadataPath = Path.Combine(_root, "global-metadata.dat");

        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(_gameAssemblyPath, "game");
        File.WriteAllText(_metadataPath, "meta");
    }

    [Fact]
    public void LoadIfFresh_ReturnsSupplement_WhenCacheIsCurrent()
    {
        var gameMtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var metaMtime = DateTimeOffset.UtcNow.AddMinutes(-9);
        File.SetLastWriteTimeUtc(_gameAssemblyPath, gameMtime.UtcDateTime);
        File.SetLastWriteTimeUtc(_metadataPath, metaMtime.UtcDateTime);

        var cached = NewSupplement(
            new DateTimeOffset(File.GetLastWriteTimeUtc(_gameAssemblyPath)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(_metadataPath)));
        WriteCache(cached);

        var loaded = Il2CppMetadataCache.LoadIfFresh(_cacheRoot, _gameAssemblyPath, _metadataPath);

        Assert.NotNull(loaded);
        Assert.Equal(cached.GameAssemblyMtime, loaded!.GameAssemblyMtime);
        Assert.Equal(cached.MetadataMtime, loaded.MetadataMtime);
    }

    [Fact]
    public void LoadIfFresh_ReturnsNull_WhenSourceFilesAreNewerThanCacheRecord()
    {
        var gameMtime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var metaMtime = DateTimeOffset.UtcNow.AddMinutes(-9);
        File.SetLastWriteTimeUtc(_gameAssemblyPath, gameMtime.UtcDateTime);
        File.SetLastWriteTimeUtc(_metadataPath, metaMtime.UtcDateTime);

        var cached = NewSupplement(
            new DateTimeOffset(File.GetLastWriteTimeUtc(_gameAssemblyPath)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(_metadataPath)));
        WriteCache(cached);

        File.SetLastWriteTimeUtc(_metadataPath, DateTime.UtcNow);

        var loaded = Il2CppMetadataCache.LoadIfFresh(_cacheRoot, _gameAssemblyPath, _metadataPath);

        Assert.Null(loaded);
    }

    [Fact]
    public void LoadIfPresent_ReturnsNull_WhenSchemaVersionMismatches()
    {
        var cached = NewSupplement(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        cached.SchemaVersion = Il2CppMetadataSupplement.CurrentSchemaVersion - 1;
        WriteCache(cached);

        var loaded = Il2CppMetadataCache.LoadIfPresent(_cacheRoot);

        Assert.Null(loaded);
    }

    [Fact]
    public void LoadIfPresent_ReturnsNull_WhenCacheJsonIsMalformed()
    {
        File.WriteAllText(Il2CppMetadataCache.GetCachePath(_cacheRoot), "{ this is not json");

        var loaded = Il2CppMetadataCache.LoadIfPresent(_cacheRoot);

        Assert.Null(loaded);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Il2CppMetadataSupplement NewSupplement(DateTimeOffset gameMtime, DateTimeOffset metadataMtime)
        => new()
        {
            SchemaVersion = Il2CppMetadataSupplement.CurrentSchemaVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            GameAssemblyMtime = gameMtime,
            MetadataMtime = metadataMtime,
        };

    private void WriteCache(Il2CppMetadataSupplement supplement)
    {
        File.WriteAllText(Il2CppMetadataCache.GetCachePath(_cacheRoot), supplement.ToJson());
    }
}
