using System.IO;
using Jiangyu.Studio.Rpc;
using Xunit;

namespace Jiangyu.Studio.Host.Tests;

// The templatesQuery catalog cache keys on the game assembly plus each code DLL's
// last-write time, so a recompile reloads the catalog with no explicit invalidation.
// These cover the key, the part of the cache whose correctness is observable in a test.
public sealed class QueryCatalogKeyTests : IDisposable
{
    private readonly string _dir;

    public QueryCatalogKeyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"jiangyu-querykey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteDll(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void SameInputs_ProduceSameKey()
    {
        var dll = WriteDll("Mod.Code.dll", "v1");
        var a = RpcHandlers.QueryCatalogKey("game/Assembly-CSharp.dll", new[] { dll });
        var b = RpcHandlers.QueryCatalogKey("game/Assembly-CSharp.dll", new[] { dll });
        Assert.Equal(a, b);
    }

    [Fact]
    public void RewritingACodeDll_ChangesTheKey()
    {
        var dll = WriteDll("Mod.Code.dll", "v1");
        var before = RpcHandlers.QueryCatalogKey("game/Assembly-CSharp.dll", new[] { dll });

        // A recompile rewrites the DLL with a newer write time.
        File.SetLastWriteTimeUtc(dll, File.GetLastWriteTimeUtc(dll).AddSeconds(5));
        var after = RpcHandlers.QueryCatalogKey("game/Assembly-CSharp.dll", new[] { dll });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DifferentCodeDllSet_ChangesTheKey()
    {
        var one = WriteDll("One.dll", "x");
        var two = WriteDll("Two.dll", "y");
        var withOne = RpcHandlers.QueryCatalogKey("g", new[] { one });
        var withBoth = RpcHandlers.QueryCatalogKey("g", new[] { one, two });
        Assert.NotEqual(withOne, withBoth);
    }

    [Fact]
    public void NoCodeDlls_KeyIsTheAssemblyPath()
    {
        Assert.Equal("g/Assembly-CSharp.dll", RpcHandlers.QueryCatalogKey("g/Assembly-CSharp.dll", []));
    }

    [Fact]
    public void CodeDllOrder_DoesNotAffectTheKey()
    {
        var one = WriteDll("One.dll", "x");
        var two = WriteDll("Two.dll", "y");
        var ab = RpcHandlers.QueryCatalogKey("g", new[] { one, two });
        var ba = RpcHandlers.QueryCatalogKey("g", new[] { two, one });
        Assert.Equal(ab, ba);
    }
}
