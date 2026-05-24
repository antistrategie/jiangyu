using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

public sealed class IndexCacheValidatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"jiangyu-cache-validator-{Guid.NewGuid():N}");

    public IndexCacheValidatorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class FakeManifest
    {
        public required string Hash { get; init; }
        public required int FormatVersion { get; init; }
    }

    [Fact]
    public void Validate_ReportsMissing_WhenCacheFileAbsent()
    {
        var cacheFile = Path.Combine(_tempDir, "missing.json");
        var manifestFile = Path.Combine(_tempDir, "missing-manifest.json");

        var status = IndexCacheValidator.Validate<FakeManifest>(
            cacheFile,
            manifestFile,
            loadManifest: _ => throw new InvalidOperationException("should not be called"),
            staleReason: _ => null,
            missingReason: "test-missing",
            unreadableManifestReason: "test-unreadable");

        Assert.Equal(CachedIndexState.Missing, status.State);
        Assert.Equal("test-missing", status.Reason);
    }

    [Fact]
    public void Validate_ReportsMissing_WhenManifestFileAbsent()
    {
        var cacheFile = Path.Combine(_tempDir, "cache.json");
        var manifestFile = Path.Combine(_tempDir, "cache-manifest.json");
        File.WriteAllText(cacheFile, "{}");

        var status = IndexCacheValidator.Validate<FakeManifest>(
            cacheFile,
            manifestFile,
            loadManifest: _ => throw new InvalidOperationException("should not be called"),
            staleReason: _ => null,
            missingReason: "test-missing",
            unreadableManifestReason: "test-unreadable");

        Assert.Equal(CachedIndexState.Missing, status.State);
    }

    [Fact]
    public void Validate_ReportsStale_WhenLoaderReturnsNull()
    {
        var cacheFile = Path.Combine(_tempDir, "cache.json");
        var manifestFile = Path.Combine(_tempDir, "cache-manifest.json");
        File.WriteAllText(cacheFile, "{}");
        File.WriteAllText(manifestFile, "garbage");

        var status = IndexCacheValidator.Validate<FakeManifest>(
            cacheFile,
            manifestFile,
            loadManifest: _ => null,
            staleReason: _ => null,
            missingReason: "test-missing",
            unreadableManifestReason: "test-unreadable");

        Assert.Equal(CachedIndexState.Stale, status.State);
        Assert.Equal("test-unreadable", status.Reason);
    }

    [Fact]
    public void Validate_ReportsStale_WhenLoaderThrowsJsonException()
    {
        // System.Text.Json.JsonException is the documented failure of a
        // deserialiser hitting malformed JSON. The helper catches it so
        // callers don't have to wrap their loader.
        var cacheFile = Path.Combine(_tempDir, "cache.json");
        var manifestFile = Path.Combine(_tempDir, "cache-manifest.json");
        File.WriteAllText(cacheFile, "{}");
        File.WriteAllText(manifestFile, "{}");

        var status = IndexCacheValidator.Validate<FakeManifest>(
            cacheFile,
            manifestFile,
            loadManifest: _ => throw new System.Text.Json.JsonException("bad json"),
            staleReason: _ => null,
            missingReason: "test-missing",
            unreadableManifestReason: "test-unreadable");

        Assert.Equal(CachedIndexState.Stale, status.State);
        Assert.Equal("test-unreadable", status.Reason);
    }

    [Fact]
    public void Validate_ReportsStale_WhenPredicateReturnsReason()
    {
        var cacheFile = Path.Combine(_tempDir, "cache.json");
        var manifestFile = Path.Combine(_tempDir, "cache-manifest.json");
        File.WriteAllText(cacheFile, "{}");
        File.WriteAllText(manifestFile, "{}");

        var manifest = new FakeManifest { Hash = "old", FormatVersion = 1 };

        var status = IndexCacheValidator.Validate<FakeManifest>(
            cacheFile,
            manifestFile,
            loadManifest: _ => manifest,
            staleReason: m => m.FormatVersion < 2 ? "version drift" : null,
            missingReason: "test-missing",
            unreadableManifestReason: "test-unreadable");

        Assert.Equal(CachedIndexState.Stale, status.State);
        Assert.Equal("version drift", status.Reason);
    }

    [Fact]
    public void Validate_ReportsCurrent_WhenPredicateReturnsNull()
    {
        var cacheFile = Path.Combine(_tempDir, "cache.json");
        var manifestFile = Path.Combine(_tempDir, "cache-manifest.json");
        File.WriteAllText(cacheFile, "{}");
        File.WriteAllText(manifestFile, "{}");

        var manifest = new FakeManifest { Hash = "current", FormatVersion = 2 };

        var status = IndexCacheValidator.Validate<FakeManifest>(
            cacheFile,
            manifestFile,
            loadManifest: _ => manifest,
            staleReason: _ => null,
            missingReason: "test-missing",
            unreadableManifestReason: "test-unreadable");

        Assert.Equal(CachedIndexState.Current, status.State);
        Assert.Null(status.Reason);
        Assert.True(status.IsCurrent);
    }
}
