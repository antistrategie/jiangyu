using System.IO;
using Jiangyu.Core.IO;
using Xunit;

namespace Jiangyu.Core.Tests.IO;

public sealed class ResilientFsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jiangyu-resilientfs-{Guid.NewGuid():N}");

    public ResilientFsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DeleteDirectory_removes_an_existing_tree()
    {
        var dir = Path.Combine(_root, "tree");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "a.txt"), "x");

        ResilientFs.DeleteDirectory(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteDirectory_is_a_no_op_when_the_path_is_absent()
    {
        var dir = Path.Combine(_root, "does-not-exist");

        ResilientFs.DeleteDirectory(dir); // must not throw

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteFile_removes_an_existing_file()
    {
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "x");

        ResilientFs.DeleteFile(file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void CreateDirectory_creates_and_is_idempotent()
    {
        var dir = Path.Combine(_root, "made");

        ResilientFs.CreateDirectory(dir);
        ResilientFs.CreateDirectory(dir); // second call must not throw

        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteDirectory_then_CreateDirectory_leaves_an_empty_tree()
    {
        var dir = Path.Combine(_root, "cycle");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "old.txt"), "x");

        ResilientFs.DeleteDirectory(dir);
        ResilientFs.CreateDirectory(dir);

        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    // Regression for the code-review finding: a genuine missing-path error (DirectoryNotFound /
    // FileNotFound) derives from IOException but is NOT a transient lock. It must surface as its
    // own exception, unwrapped and without burning the retry backoff, rather than being reported
    // as the misleading "locked by another process, close MENACE" lock error.
    [Fact]
    public void DeleteFile_with_a_missing_parent_directory_is_not_treated_as_a_lock()
    {
        var missing = Path.Combine(_root, "no-such-dir", "a.txt");

        var ex = Assert.ThrowsAny<IOException>(() => ResilientFs.DeleteFile(missing));

        Assert.IsType<DirectoryNotFoundException>(ex);
        Assert.DoesNotContain("locked by another process", ex.Message);
    }
}
