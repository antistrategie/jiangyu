using Jiangyu.Core.Templates;
using Jiangyu.Core.Tests.Templates.Fixtures.Gameplay;

namespace Jiangyu.Core.Tests.Templates;

public class TemplateTypeCatalogTests
{
    private static string FixtureAssemblyPath =>
        typeof(FixtureEntity).Assembly.Location;

    private static TemplateTypeCatalog Load() => TemplateTypeCatalog.Load(FixtureAssemblyPath);

    [Fact]
    public void ResolveType_AcceptsShortName()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out var error);
        Assert.Null(error);
        Assert.NotNull(type);
        Assert.Equal("FixtureEntity", type!.Name);
    }

    [Fact]
    public void ResolveType_AcceptsFullyQualifiedName()
    {
        using var catalog = Load();
        var type = catalog.ResolveType(
            "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity",
            out _,
            out var error);
        Assert.Null(error);
        Assert.NotNull(type);
        Assert.Equal("FixtureEntity", type!.Name);
    }

    [Fact]
    public void ResolveType_ReportsAmbiguousShortName()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureSkill", out var candidates, out var error);
        Assert.Null(type);
        Assert.NotNull(error);
        Assert.Contains("ambiguous", error);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void ResolveType_ReportsMissing()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("Nope", out _, out var error);
        Assert.Null(type);
        Assert.NotNull(error);
        Assert.Contains("no type 'Nope'", error);
    }

    [Fact]
    public void GetMembers_WritableOnlyByDefault()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type);

        Assert.All(members, m => Assert.True(m.IsWritable));
        Assert.Contains(members, m => m.Name == "Properties");
        Assert.Contains(members, m => m.Name == "Skills");
        Assert.Contains(members, m => m.Name == "m_ID" && m.IsInherited);
        Assert.DoesNotContain(members, m => m.Name == "ReadOnlyCount");
    }

    [Fact]
    public void GetMembers_IncludesReadOnlyWhenRequested()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type, includeReadOnly: true);

        Assert.Contains(members, m => m.Name == "ReadOnlyCount" && !m.IsWritable);
    }

    [Fact]
    public void GetMembers_WalksBaseChain()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type);

        // m_ID comes from FixtureBaseEntity
        var inheritedId = members.FirstOrDefault(m => m.Name == "m_ID");
        Assert.NotNull(inheritedId);
        Assert.True(inheritedId!.IsInherited);
        Assert.EndsWith("FixtureBaseEntity", inheritedId.DeclaringTypeFullName);
    }

    [Fact]
    public void GetElementType_UnwrapsList()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type);
        var skills = members.Single(m => m.Name == "Skills");

        var element = catalog.GetElementType(skills.MemberType);
        Assert.NotNull(element);
        Assert.Equal("FixtureSkill", element!.Name);
    }

    [Fact]
    public void GetElementType_UnwrapsArray()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type);
        var bones = members.Single(m => m.Name == "BoneIndices");

        var element = catalog.GetElementType(bones.MemberType);
        Assert.NotNull(element);
        Assert.Equal("Int32", element!.Name);
    }

    [Fact]
    public void GetElementType_ReturnsNullForNonCollection()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type);
        var properties = members.Single(m => m.Name == "Properties");

        Assert.Null(catalog.GetElementType(properties.MemberType));
    }

    [Fact]
    public void IsScalar_ReturnsTrueForPrimitives_StringsAndEnums()
    {
        using var catalog = Load();
        var entity = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var props = catalog.ResolveType("FixtureProperties", out _, out _)!;

        var entityMembers = catalog.GetMembers(entity);
        var propsMembers = catalog.GetMembers(props);

        Assert.True(catalog.IsScalar(entityMembers.Single(m => m.Name == "IsEnabled").MemberType));
        Assert.True(catalog.IsScalar(entityMembers.Single(m => m.Name == "HudYOffsetScale").MemberType));
        Assert.True(catalog.IsScalar(propsMembers.Single(m => m.Name == "Accuracy").MemberType));
        Assert.True(catalog.IsScalar(propsMembers.Single(m => m.Name == "DisplayName").MemberType));
        Assert.True(catalog.IsScalar(propsMembers.Single(m => m.Name == "DamageType").MemberType));

        Assert.False(catalog.IsScalar(entityMembers.Single(m => m.Name == "Properties").MemberType));
        Assert.False(catalog.IsScalar(entityMembers.Single(m => m.Name == "Skills").MemberType));
    }

    [Fact]
    public void FriendlyName_ShortensGenericsAndArrays()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = catalog.GetMembers(type);

        Assert.Equal("List<FixtureSkill>", catalog.FriendlyName(members.Single(m => m.Name == "Skills").MemberType));
        Assert.Equal("Int32[]", catalog.FriendlyName(members.Single(m => m.Name == "BoneIndices").MemberType));
        Assert.Equal("Single", catalog.FriendlyName(members.Single(m => m.Name == "HudYOffsetScale").MemberType));
    }
}
