using Jiangyu.Core.Abstractions;
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

    [Fact]
    public void TryResolveGamePaths_PrefersTheNativeAssembly_ThenTheDll()
    {
        var dataPath = MakeGameLayout(withSo: true, withDll: true, withMetadata: true);

        Assert.True(Il2CppMetadataCache.TryResolveGamePaths(dataPath, out var assembly, out var metadata));
        Assert.EndsWith("GameAssembly.so", assembly);
        Assert.EndsWith(Path.Combine("il2cpp_data", "Metadata", "global-metadata.dat"), metadata);

        File.Delete(assembly);
        Assert.True(Il2CppMetadataCache.TryResolveGamePaths(dataPath, out assembly, out _));
        Assert.EndsWith("GameAssembly.dll", assembly);
    }

    [Fact]
    public void TryResolveGamePaths_FailsWithoutTheMetadataFile()
    {
        var dataPath = MakeGameLayout(withSo: false, withDll: true, withMetadata: false);

        Assert.False(Il2CppMetadataCache.TryResolveGamePaths(dataPath, out _, out _));
    }

    [Fact]
    public void BuildIfStale_KeepsAFreshSupplement_WithoutProbingTheVersion()
    {
        var dataPath = MakeGameLayout(withSo: false, withDll: true, withMetadata: true);
        Assert.True(Il2CppMetadataCache.TryResolveGamePaths(dataPath, out var assembly, out var metadata));
        WriteCache(NewSupplement(
            new DateTimeOffset(File.GetLastWriteTimeUtc(assembly)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(metadata))));
        var probed = false;

        var fresh = Il2CppMetadataCache.BuildIfStale(_cacheRoot, dataPath, () => { probed = true; return null; }, NullLogSink.Instance);

        Assert.True(fresh);
        Assert.False(probed);
    }

    [Fact]
    public void BuildIfStale_RebuildsAFreshSupplement_WhenForced()
    {
        var dataPath = MakeGameLayout(withSo: false, withDll: true, withMetadata: true);
        Assert.True(Il2CppMetadataCache.TryResolveGamePaths(dataPath, out var assembly, out var metadata));
        WriteCache(NewSupplement(
            new DateTimeOffset(File.GetLastWriteTimeUtc(assembly)),
            new DateTimeOffset(File.GetLastWriteTimeUtc(metadata))));
        var probed = false;

        // The probe runs (and here declines), so the forced path went past
        // the freshness check. The fresh supplement it failed to replace is
        // still in place, which is what the return value reports.
        var fresh = Il2CppMetadataCache.BuildIfStale(_cacheRoot, dataPath, () => { probed = true; return null; }, NullLogSink.Instance, force: true);

        Assert.True(probed);
        Assert.True(fresh);
    }

    [Fact]
    public void BuildIfStale_ReportsFalse_WhenTheGameFilesAreMissing()
    {
        var dataPath = MakeGameLayout(withSo: false, withDll: false, withMetadata: false);

        Assert.False(Il2CppMetadataCache.BuildIfStale(_cacheRoot, dataPath, () => null, NullLogSink.Instance));
    }

    [Fact]
    public void BuildIfStale_ReportsFalse_WhenTheVersionProbeThrows()
    {
        // A stale or missing supplement plus a probe failure degrades to "no
        // supplement", never to an exception out of the caller's command.
        var dataPath = MakeGameLayout(withSo: false, withDll: true, withMetadata: true);

        var fresh = Il2CppMetadataCache.BuildIfStale(_cacheRoot, dataPath, () => throw new IOException("locked"), NullLogSink.Instance);

        Assert.False(fresh);
    }

    // <root>/game/GameAssembly.{so,dll} beside <root>/game/Game_Data/il2cpp_data/Metadata/global-metadata.dat.
    private string MakeGameLayout(bool withSo, bool withDll, bool withMetadata)
    {
        var gameRoot = Path.Combine(_root, "game");
        var dataPath = Path.Combine(gameRoot, "Game_Data");
        Directory.CreateDirectory(dataPath);
        if (withSo) File.WriteAllText(Path.Combine(gameRoot, "GameAssembly.so"), "so");
        if (withDll) File.WriteAllText(Path.Combine(gameRoot, "GameAssembly.dll"), "dll");
        if (withMetadata)
        {
            var metadataDir = Path.Combine(dataPath, "il2cpp_data", "Metadata");
            Directory.CreateDirectory(metadataDir);
            File.WriteAllText(Path.Combine(metadataDir, "global-metadata.dat"), "meta");
        }
        return dataPath;
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
