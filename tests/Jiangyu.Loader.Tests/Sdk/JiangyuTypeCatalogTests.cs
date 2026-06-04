using System.Linq;
using Jiangyu.Loader.Sdk;
using Jiangyu.Loader.Sdk.Types;
using Jiangyu.Sdk;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk;

public class JiangyuTypeCatalogTests
{
    [JiangyuType]
    private sealed class Alpha { }

    [JiangyuType("custom-name")]
    private sealed class Beta { }

    [JiangyuType]
    private abstract class AbstractType { }

    [JiangyuType]
    private sealed class GenericType<T> { }

    [JiangyuType("dup")]
    private sealed class DupA { }

    [JiangyuType("dup")]
    private sealed class DupB { }

    [Fact]
    public void Scan_resolves_ns_name_from_class_name_and_attribute()
    {
        var scan = JiangyuTypeCatalog.Scan(typeof(Alpha).Assembly, "mymod");

        Assert.Contains(scan.Entries, e => e.QualifiedName == "mymod:Alpha" && e.ManagedType == typeof(Alpha));
        Assert.Contains(scan.Entries, e => e.QualifiedName == "mymod:custom-name" && e.ManagedType == typeof(Beta));
    }

    [Fact]
    public void Scan_rejects_abstract_types()
    {
        var scan = JiangyuTypeCatalog.Scan(typeof(Alpha).Assembly, "mymod");

        Assert.DoesNotContain(scan.Entries, e => e.ManagedType == typeof(AbstractType));
        Assert.Contains(scan.Errors, x => x.Contains("AbstractType") && x.Contains("concrete"));
    }

    [Fact]
    public void Scan_rejects_generic_types()
    {
        var scan = JiangyuTypeCatalog.Scan(typeof(Alpha).Assembly, "mymod");

        Assert.DoesNotContain(scan.Entries, e => e.ManagedType == typeof(GenericType<>));
        Assert.Contains(scan.Errors, x => x.Contains("GenericType") && x.Contains("generic"));
    }

    [Fact]
    public void Scan_rejects_name_collisions()
    {
        var scan = JiangyuTypeCatalog.Scan(typeof(Alpha).Assembly, "mymod");

        Assert.Contains(scan.Errors, x => x.Contains("collision") && x.Contains("mymod:dup"));
        Assert.Single(scan.Entries, e => e.QualifiedName == "mymod:dup");
    }

    [Fact]
    public void Scan_rejects_invalid_mod_id()
    {
        var scan = JiangyuTypeCatalog.Scan(typeof(Alpha).Assembly, "bad:id");

        Assert.Empty(scan.Entries);
        Assert.Contains(scan.Errors, x => x.Contains("invalid mod id"));
    }
}
