using System;
using System.Linq;
using Jiangyu.Core.Code;
using Jiangyu.Core.Templates;
using Xunit;

namespace Jiangyu.Core.Tests.Code;

// Stand-in for Jiangyu.Sdk.JiangyuTypeAttribute. The reader matches the attribute
// by its simple name, so a local one lets the fixtures carry [JiangyuType] without
// the test assembly depending on the SDK.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class JiangyuTypeAttribute : Attribute
{
    public JiangyuTypeAttribute(string? name = null) => Name = name;
    public string? Name { get; }
}

internal abstract class FixtureCodeBase { }

[JiangyuType]
internal sealed class FixtureCodeType : FixtureCodeBase { }

[JiangyuType("CustomName")]
internal sealed class FixtureNamedCodeType : FixtureCodeBase { }

public class CodeTypeResolverTests
{
    // Primary = Jiangyu.Core.dll, a stand-in game assembly; the extra to scan is THIS
    // test assembly, which carries the [JiangyuType] fixtures above.
    private static TemplateTypeCatalog LoadWithFixtureCode()
        => TemplateTypeCatalog.Load(
            typeof(TemplateTypeCatalog).Assembly.Location,
            additionalAssembliesToScan: new[] { typeof(CodeTypeResolverTests).Assembly.Location });

    [Fact]
    public void ScannedExtraTypes_HoldsCodeTypesNotGameTypes()
    {
        using var catalog = LoadWithFixtureCode();
        Assert.Contains(catalog.ScannedExtraTypes, t => t.Name == "FixtureCodeType");
        Assert.DoesNotContain(catalog.ScannedExtraTypes, t => t == typeof(TemplateTypeCatalog));
    }

    [Fact]
    public void ResolveFullName_MatchesByJiangyuTypeBareName()
    {
        using var catalog = LoadWithFixtureCode();
        Assert.Equal(typeof(FixtureCodeType).FullName, CodeTypeResolver.ResolveFullName(catalog, "FixtureCodeType"));
        // The attribute's name argument overrides the class name.
        Assert.Equal(typeof(FixtureNamedCodeType).FullName, CodeTypeResolver.ResolveFullName(catalog, "CustomName"));
        Assert.Null(CodeTypeResolver.ResolveFullName(catalog, "NotAType"));
    }

    [Fact]
    public void QualifiedName_PrefixesCodeTypesWithModId()
    {
        using var catalog = LoadWithFixtureCode();
        var codeType = catalog.ScannedExtraTypes.First(t => t.Name == "FixtureCodeType");
        var named = catalog.ScannedExtraTypes.First(t => t.Name == "FixtureNamedCodeType");

        Assert.Equal("mymod:FixtureCodeType", CodeTypeResolver.QualifiedName(catalog, codeType, "mymod"));
        Assert.Equal("mymod:CustomName", CodeTypeResolver.QualifiedName(catalog, named, "mymod"));
        // No mod id -> no qualified name; the caller falls back to the friendly name.
        Assert.Null(CodeTypeResolver.QualifiedName(catalog, codeType, null));
        // A game type is not a code type.
        Assert.Null(CodeTypeResolver.QualifiedName(catalog, typeof(TemplateTypeCatalog), "mymod"));
    }

    [Fact]
    public void EnumerateConcreteSubtypes_IncludesScannedCodeTypes()
    {
        using var catalog = LoadWithFixtureCode();
        var baseType = catalog.ScannedExtraTypes.First(t => t.Name == "FixtureCodeType").BaseType!;

        var subtypes = catalog.EnumerateConcreteSubtypes(baseType);
        Assert.Contains(subtypes, t => t.Name == "FixtureCodeType");
        Assert.Contains(subtypes, t => t.Name == "FixtureNamedCodeType");
    }
}
