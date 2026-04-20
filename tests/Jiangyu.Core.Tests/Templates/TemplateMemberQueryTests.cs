using Jiangyu.Core.Templates;
using Jiangyu.Core.Tests.Templates.Fixtures.Gameplay;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class TemplateMemberQueryTests
{
    private static string FixtureAssemblyPath =>
        typeof(FixtureEntity).Assembly.Location;

    private static TemplateTypeCatalog Load() => TemplateTypeCatalog.Load(FixtureAssemblyPath);

    [Fact]
    public void TypeOnly_ReturnsTypeNode()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity");

        Assert.Equal(QueryResultKind.TypeNode, result.Kind);
        Assert.Equal("FixtureEntity", result.CurrentType!.Name);
        Assert.Contains(result.Members!, m => m.Name == "Properties");
    }

    [Fact]
    public void NestedWrapper_ReturnsTypeNode()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Properties");

        Assert.Equal(QueryResultKind.TypeNode, result.Kind);
        Assert.Equal("FixtureProperties", result.CurrentType!.Name);
        Assert.Contains(result.Members!, m => m.Name == "Accuracy");
    }

    [Fact]
    public void CollectionWithoutIndexer_AutoUnwraps()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Skills");

        Assert.Equal(QueryResultKind.TypeNode, result.Kind);
        Assert.Equal("FixtureSkillTemplate", result.CurrentType!.Name);
        Assert.NotNull(result.UnwrappedFrom);
        Assert.Contains(result.Members!, m => m.Name == "Uses");
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, result.PatchScalarKind);
        Assert.Equal("FixtureSkillTemplate", result.ReferenceTargetTypeName);
    }

    [Fact]
    public void CollectionWithIndexer_AutoUnwraps()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Skills[0]");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal("FixtureSkillTemplate", result.CurrentType!.Name);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, result.PatchScalarKind);
    }

    [Fact]
    public void ScalarLeaf_ReturnsLeafWithScalarKind()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Properties.Accuracy");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal("Int32", result.CurrentType!.Name);
        Assert.Equal(CompiledTemplateValueKind.Int32, result.PatchScalarKind);
        Assert.True(result.IsWritable);
    }

    [Fact]
    public void SingleLeaf_ReturnsSingleScalar()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.HudYOffsetScale");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.Single, result.PatchScalarKind);
    }

    [Fact]
    public void EnumLeaf_ReturnsEnumScalar()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Properties.DamageType");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.Enum, result.PatchScalarKind);
        Assert.Equal(["Ballistic", "Blunt", "Plasma"], result.EnumMemberNames);
    }

    [Fact]
    public void StringLeaf_ReturnsStringScalar()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Properties.DisplayName");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.String, result.PatchScalarKind);
    }

    [Fact]
    public void BooleanLeaf_ReturnsBooleanScalar()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.IsEnabled");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.Boolean, result.PatchScalarKind);
    }

    [Fact]
    public void IndexedLeaf_AfterUnwrap_WorksForElementScalar()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Skills[0].Uses");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.Int32, result.PatchScalarKind);
        Assert.Equal("FixtureEntity.Skills[0].Uses", result.ResolvedPath);
    }

    [Fact]
    public void TerminalIndexedArrayElement_ResolvesToScalarLeaf()
    {
        // Direct terminal write into a non-byte scalar array element — the
        // applier binds via TryBindArrayElement, and the CLI surface must
        // label the scalar kind so modders see the right patch shape.
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.BoneIndices[0]");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.Int32, result.PatchScalarKind);
        Assert.Equal("FixtureEntity.BoneIndices[0]", result.ResolvedPath);
    }

    [Fact]
    public void DirectTemplateReferenceField_RetainsTypeNavigation_AndPatchValueKind()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.InitialSkill");

        Assert.Equal(QueryResultKind.TypeNode, result.Kind);
        Assert.Equal("FixtureSkillTemplate", result.CurrentType!.Name);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, result.PatchScalarKind);
        Assert.Equal("FixtureSkillTemplate", result.ReferenceTargetTypeName);
    }

    [Fact]
    public void FullyQualifiedPrefix_Resolves()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(
            catalog,
            "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity.Properties.Accuracy");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal(CompiledTemplateValueKind.Int32, result.PatchScalarKind);
    }

    [Fact]
    public void UnknownType_Errors()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "Nope.Whatever");

        Assert.Equal(QueryResultKind.Error, result.Kind);
        Assert.Contains("no type prefix", result.ErrorMessage!);
    }

    [Fact]
    public void AmbiguousShortName_Errors()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureSkillTemplate");

        Assert.Equal(QueryResultKind.Error, result.Kind);
        Assert.Contains("ambiguous", result.ErrorMessage!);
    }

    [Fact]
    public void UnknownMember_Errors()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.NotThere");

        Assert.Equal(QueryResultKind.Error, result.Kind);
        Assert.Contains("'NotThere'", result.ErrorMessage!);
    }

    [Fact]
    public void IndexerOnScalar_Errors()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.IsEnabled[0]");

        Assert.Equal(QueryResultKind.Error, result.Kind);
        Assert.Contains("indexer applied", result.ErrorMessage!);
    }

    [Fact]
    public void EmptyQuery_Errors()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "");

        Assert.Equal(QueryResultKind.Error, result.Kind);
    }

    [Fact]
    public void LeadingDot_Errors()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, ".FixtureEntity");

        Assert.Equal(QueryResultKind.Error, result.Kind);
    }

    [Fact]
    public void ResolvedPath_PreservesOriginalSegments()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.Skills[3]");

        Assert.Equal(QueryResultKind.Leaf, result.Kind);
        Assert.Equal("FixtureEntity.Skills[3]", result.ResolvedPath);
    }

    [Fact]
    public void OdinAnnotatedMember_IsFlagged()
    {
        using var catalog = Load();
        var result = TemplateMemberQuery.Run(catalog, "FixtureEntity.CustomCondition");

        Assert.Equal(QueryResultKind.TypeNode, result.Kind);
        Assert.True(result.IsLikelyOdinOnly);
    }
}
