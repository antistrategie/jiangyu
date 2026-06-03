using System;
using System.IO;
using Jiangyu.Loader.Runtime;
using Xunit;

namespace Jiangyu.Loader.Tests.Runtime;

public sealed class JiangyuModResolveModIdTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jiangyu-modid-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Prefers_the_manifest_name_over_the_folder_name()
    {
        // A renamed Mods/<folder> still resolves its types under the manifest's name.
        var folder = Path.Combine(_root, "renamed-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "jiangyu.json"), "{ \"name\": \"RealModId\" }");

        Assert.Equal("RealModId", JiangyuMod.ResolveModId(folder));
    }

    [Fact]
    public void Falls_back_to_the_folder_name_when_no_manifest()
    {
        var folder = Path.Combine(_root, "no-manifest-mod");
        Directory.CreateDirectory(folder);

        Assert.Equal("no-manifest-mod", JiangyuMod.ResolveModId(folder));
    }

    [Fact]
    public void Falls_back_to_the_folder_name_when_the_manifest_name_is_blank()
    {
        var folder = Path.Combine(_root, "blank-name-mod");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "jiangyu.json"), "{ \"name\": \"\" }");

        Assert.Equal("blank-name-mod", JiangyuMod.ResolveModId(folder));
    }
}
