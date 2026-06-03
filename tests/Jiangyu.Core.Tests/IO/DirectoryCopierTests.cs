using System.IO;
using Jiangyu.Core.IO;
using Xunit;

namespace Jiangyu.Core.Tests.IO;

public sealed class DirectoryCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jiangyu-copier-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Copy_recreates_the_tree_including_nested_directories()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");

        var dest = Path.Combine(_root, "dest");
        DirectoryCopier.Copy(src, dest, overwrite: false);

        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "sub", "b.txt")));
    }

    [Fact]
    public void Copy_overwrite_true_replaces_an_existing_destination_file()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "new");

        var dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "old");

        DirectoryCopier.Copy(src, dest, overwrite: true);

        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void Copy_overwrite_false_throws_when_a_destination_file_exists()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "new");

        var dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "old");

        Assert.Throws<IOException>(() => DirectoryCopier.Copy(src, dest, overwrite: false));
    }
}
