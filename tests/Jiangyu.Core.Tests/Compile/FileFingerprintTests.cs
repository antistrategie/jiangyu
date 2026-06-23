using Jiangyu.Core.Compile;

namespace Jiangyu.Core.Tests.Compile;

public class FileFingerprintTests : IDisposable
{
    private readonly string _dir;

    public FileFingerprintTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"jiangyu-fp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void OfDirectory_IsStableForUnchangedContent()
    {
        Write("a.txt", "alpha");
        Write("sub/b.txt", "beta");

        var first = FileFingerprint.OfDirectory(_dir);
        var second = FileFingerprint.OfDirectory(_dir);

        Assert.NotEqual(string.Empty, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void OfDirectory_ChangesWhenContentChanges()
    {
        Write("a.txt", "alpha");
        var before = FileFingerprint.OfDirectory(_dir);

        Write("a.txt", "alpha-edited");
        Assert.NotEqual(before, FileFingerprint.OfDirectory(_dir));
    }

    [Fact]
    public void OfDirectory_ChangesWhenFileAddedOrRemoved()
    {
        Write("a.txt", "alpha");
        var oneFile = FileFingerprint.OfDirectory(_dir);

        Write("b.txt", "beta");
        var twoFiles = FileFingerprint.OfDirectory(_dir);
        Assert.NotEqual(oneFile, twoFiles);

        File.Delete(Path.Combine(_dir, "b.txt"));
        Assert.Equal(oneFile, FileFingerprint.OfDirectory(_dir));
    }

    [Fact]
    public void OfDirectory_DistinguishesPathFromContent()
    {
        // Same bytes under different names must hash differently (path is part of the input).
        Write("a.txt", "x");
        var asA = FileFingerprint.OfDirectory(_dir);

        File.Delete(Path.Combine(_dir, "a.txt"));
        Write("b.txt", "x");
        Assert.NotEqual(asA, FileFingerprint.OfDirectory(_dir));
    }

    [Fact]
    public void OfDirectory_EmptyForMissingDir()
    {
        Assert.Equal(string.Empty, FileFingerprint.OfDirectory(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void OfDirectory_ExcludedFilesDoNotAffectHash()
    {
        // A file matched by the exclude predicate must not contribute to the hash, so the
        // fingerprint equals one taken with the excluded file absent entirely.
        static bool Excluded(string rel) => rel.StartsWith("Jiangyu/Staging/", StringComparison.Ordinal);

        Write("Assets/a.txt", "alpha");
        var sourceOnly = FileFingerprint.OfDirectory(_dir, Excluded);

        Write("Jiangyu/Staging/staged.txt", "build-output");
        Assert.Equal(sourceOnly, FileFingerprint.OfDirectory(_dir, Excluded));
    }

    [Fact]
    public void OfDirectory_StableWhenOnlyExcludedSubtreeChanges()
    {
        // The regression: the compile rewrites Assets/Jiangyu/Staging/ every run, so churn there
        // must leave the fingerprint unchanged while real source edits still move it.
        static bool Excluded(string rel) => rel.StartsWith("Jiangyu/Staging/", StringComparison.Ordinal);

        Write("Authored/model.txt", "source");
        Write("Jiangyu/Staging/clip.wav", "v1");
        var first = FileFingerprint.OfDirectory(_dir, Excluded);

        Write("Jiangyu/Staging/clip.wav", "v2-regenerated");
        Write("Jiangyu/Staging/extra.wav.meta", "guid: deadbeef");
        Assert.Equal(first, FileFingerprint.OfDirectory(_dir, Excluded));

        Write("Authored/model.txt", "source-edited");
        Assert.NotEqual(first, FileFingerprint.OfDirectory(_dir, Excluded));
    }

    [Fact]
    public void OfDirectory_ExcludePredicateReceivesForwardSlashedRelativePath()
    {
        // The predicate must see forward-slashed paths relative to the root on every platform,
        // so prefix matches like "Jiangyu/Staging/" work regardless of the OS path separator.
        var seen = new List<string>();
        Write("Jiangyu/Staging/clip.wav", "x");
        FileFingerprint.OfDirectory(_dir, rel => { seen.Add(rel); return false; });

        Assert.Contains("Jiangyu/Staging/clip.wav", seen);
    }

    [Fact]
    public void OfFile_PresentVsAbsent()
    {
        Write("a.txt", "alpha");
        Assert.NotEqual(string.Empty, FileFingerprint.OfFile(Path.Combine(_dir, "a.txt")));
        Assert.Equal(string.Empty, FileFingerprint.OfFile(Path.Combine(_dir, "missing.txt")));
    }

    [Fact]
    public void Combine_IsOrderSensitive()
    {
        Assert.NotEqual(FileFingerprint.Combine("a", "b"), FileFingerprint.Combine("b", "a"));
        Assert.Equal(FileFingerprint.Combine("a", "b"), FileFingerprint.Combine("a", "b"));
    }
}
