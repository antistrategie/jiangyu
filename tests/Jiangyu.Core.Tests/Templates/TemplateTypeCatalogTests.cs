using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Tests.Templates.Fixtures.Gameplay;

namespace Jiangyu.Core.Tests.Templates;

public class TemplateTypeCatalogTests
{
    private static string FixtureAssemblyPath =>
        typeof(FixtureEntity).Assembly.Location;

    private static TemplateTypeCatalog Load() => TemplateTypeCatalog.Load(FixtureAssemblyPath);

    private static TemplateTypeCatalog LoadWithSupplement(Il2CppMetadataSupplement supplement)
        => TemplateTypeCatalog.Load(FixtureAssemblyPath, supplement: supplement);

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
        var type = catalog.ResolveType("FixtureSkillTemplate", out var candidates, out var error);
        Assert.Null(type);
        Assert.NotNull(error);
        Assert.Contains("ambiguous", error);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void ResolveType_NamespaceHintDisambiguatesShortName()
    {
        using var catalog = Load();
        var type = catalog.ResolveType(
            "FixtureSkillTemplate",
            out _,
            out var error,
            namespaceHint: "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay");
        Assert.Null(error);
        Assert.NotNull(type);
        Assert.Equal("Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureSkillTemplate", type!.FullName);
    }

    [Fact]
    public void ResolveType_NamespaceHintMatchesIl2CppWrappedCandidate()
    {
        // Script assets report the unwrapped namespace; runtime assembly
        // types are wrapped under Il2Cpp*. The hint here is the unwrapped
        // form, and only the Il2Cpp-prefixed twin matches it (after the
        // resolver strips the prefix). The plain twin is in a different
        // tail namespace (Fixtures.Plain) so it does not satisfy the hint.
        using var catalog = Load();
        var type = catalog.ResolveType(
            "FixtureWrappedTwin",
            out _,
            out var error,
            namespaceHint: "Jiangyu.Core.Tests.Templates.Fixtures.Wrapped");

        Assert.Null(error);
        Assert.NotNull(type);
        Assert.Equal(
            "Il2CppJiangyu.Core.Tests.Templates.Fixtures.Wrapped.FixtureWrappedTwin",
            type!.FullName);
    }

    [Fact]
    public void ResolveType_NamespaceHintMatchesPlainCandidate()
    {
        // Symmetric direction: hint matches the plain twin directly without
        // any prefix stripping, while the Il2Cpp-prefixed twin's namespace
        // (Il2CppJiangyu.*.Wrapped) does not match this hint after stripping.
        using var catalog = Load();
        var type = catalog.ResolveType(
            "FixtureWrappedTwin",
            out _,
            out var error,
            namespaceHint: "Jiangyu.Core.Tests.Templates.Fixtures.Plain");

        Assert.Null(error);
        Assert.NotNull(type);
        Assert.Equal(
            "Jiangyu.Core.Tests.Templates.Fixtures.Plain.FixtureWrappedTwin",
            type!.FullName);
    }

    [Fact]
    public void ResolveType_NamespaceHintDoesNotOverStripNonIl2CppNamespaces()
    {
        // Negative guard: a hint that would only match if the resolver
        // stripped a non-Il2Cpp prefix from the candidate must NOT match.
        using var catalog = Load();
        var type = catalog.ResolveType(
            "FixtureSkillTemplate",
            out _,
            out _,
            namespaceHint: "Core.Tests.Templates.Fixtures.Gameplay");
        Assert.Null(type);
    }

    [Fact]
    public void ResolveType_NamespaceHintFallsBackToAmbiguityWhenMiss()
    {
        using var catalog = Load();
        var type = catalog.ResolveType(
            "FixtureSkillTemplate",
            out var candidates,
            out var error,
            namespaceHint: "Some.Namespace.That.Does.Not.Exist");
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
        var members = TemplateTypeCatalog.GetMembers(type);

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
        var members = TemplateTypeCatalog.GetMembers(type, includeReadOnly: true);

        Assert.Contains(members, m => m.Name == "ReadOnlyCount" && !m.IsWritable);
    }

    [Fact]
    public void GetMembers_WalksBaseChain()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);

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
        var members = TemplateTypeCatalog.GetMembers(type);
        var skills = members.Single(m => m.Name == "Skills");

        var element = TemplateTypeCatalog.GetElementType(skills.MemberType);
        Assert.NotNull(element);
        Assert.Equal("FixtureSkillTemplate", element!.Name);
        Assert.True(TemplateTypeCatalog.IsTemplateReferenceTarget(element));
    }

    [Fact]
    public void GetElementType_UnwrapsArray()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);
        var bones = members.Single(m => m.Name == "BoneIndices");

        var element = TemplateTypeCatalog.GetElementType(bones.MemberType);
        Assert.NotNull(element);
        Assert.Equal("Int32", element!.Name);
    }

    [Fact]
    public void GetElementType_ReturnsNullForNonCollection()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);
        var properties = members.Single(m => m.Name == "Properties");

        Assert.Null(TemplateTypeCatalog.GetElementType(properties.MemberType));
    }

    [Fact]
    public void IsScalar_ReturnsTrueForPrimitives_StringsAndEnums()
    {
        using var catalog = Load();
        var entity = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var props = catalog.ResolveType("FixtureProperties", out _, out _)!;

        var entityMembers = TemplateTypeCatalog.GetMembers(entity);
        var propsMembers = TemplateTypeCatalog.GetMembers(props);

        Assert.True(TemplateTypeCatalog.IsScalar(entityMembers.Single(m => m.Name == "IsEnabled").MemberType));
        Assert.True(TemplateTypeCatalog.IsScalar(entityMembers.Single(m => m.Name == "HudYOffsetScale").MemberType));
        Assert.True(TemplateTypeCatalog.IsScalar(propsMembers.Single(m => m.Name == "Accuracy").MemberType));
        Assert.True(TemplateTypeCatalog.IsScalar(propsMembers.Single(m => m.Name == "DisplayName").MemberType));
        Assert.True(TemplateTypeCatalog.IsScalar(propsMembers.Single(m => m.Name == "DamageType").MemberType));

        Assert.False(TemplateTypeCatalog.IsScalar(entityMembers.Single(m => m.Name == "Properties").MemberType));
        Assert.False(TemplateTypeCatalog.IsScalar(entityMembers.Single(m => m.Name == "Skills").MemberType));
    }

    [Fact]
    public void FriendlyName_ShortensGenericsAndArrays()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);

        Assert.Equal("List<FixtureSkillTemplate>", catalog.FriendlyName(members.Single(m => m.Name == "Skills").MemberType));
        Assert.Equal("Int32[]", catalog.FriendlyName(members.Single(m => m.Name == "BoneIndices").MemberType));
        Assert.Equal("Single", catalog.FriendlyName(members.Single(m => m.Name == "HudYOffsetScale").MemberType));
    }

    [Fact]
    public void GetMembers_FlagsLikelyOdinOnlyMembers()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type, includeReadOnly: true);

        // Attribute-based detection (existing)
        Assert.Contains(members, member => member.Name == "CustomCondition" && member.IsLikelyOdinOnly);
        Assert.DoesNotContain(members, member => member.Name == "InitialSkill" && member.IsLikelyOdinOnly);

        // Type-based detection: interface member
        Assert.Contains(members, member => member.Name == "AoEShape" && member.IsLikelyOdinOnly);

        // Type-based detection: abstract non-Unity class
        Assert.Contains(members, member => member.Name == "Projectile" && member.IsLikelyOdinOnly);

        // Type-based detection: non-Unity-serialisable collection (HashSet)
        Assert.Contains(members, member => member.Name == "SkillsRemoved" && member.IsLikelyOdinOnly);

        // Type-based detection: array of interface
        Assert.Contains(members, member => member.Name == "AoEShapes" && member.IsLikelyOdinOnly);

        // Abstract ScriptableObject subclass — Unity CAN serialise this as a reference
        Assert.DoesNotContain(members, member => member.Name == "ScriptableRef" && member.IsLikelyOdinOnly);
    }

    [Fact]
    public void GetEnumMemberNames_ReturnsStableNames()
    {
        using var catalog = Load();
        var type = catalog.ResolveType("FixtureDamageType", out _, out _)!;

        Assert.Equal(["Ballistic", "Blunt", "Plasma"], TemplateTypeCatalog.GetEnumMemberNames(type));
    }

    [Fact]
    public void EnrichMembers_OverlaysNamedArrayPairingsFromSupplement()
    {
        var supplement = new Il2CppMetadataSupplement
        {
            NamedArrays =
            [
                new NamedArrayPairing
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity",
                    FieldName = "BoneIndices",
                    EnumTypeShortName = "FixtureAttribute",
                },
            ],
        };

        using var catalog = LoadWithSupplement(supplement);
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);

        var enriched = catalog.EnrichMembers(type, members);

        var boneIndices = enriched.Single(member => member.Name == "BoneIndices");
        Assert.Equal("FixtureAttribute", boneIndices.NamedArrayEnumTypeName);
    }

    [Fact]
    public void EnrichMembers_OverlaysFieldMetadataHintsFromSupplement()
    {
        var supplement = new Il2CppMetadataSupplement
        {
            Fields =
            [
                new FieldMetadata
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity",
                    FieldName = "HudYOffsetScale",
                    RangeMin = 0.1,
                    RangeMax = 3.5,
                    Tooltip = "HUD offset factor",
                },
                new FieldMetadata
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity",
                    FieldName = "InitialSkill",
                    HideInInspector = true,
                    IsSoundId = true,
                },
            ],
        };

        using var catalog = LoadWithSupplement(supplement);
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);

        var enriched = catalog.EnrichMembers(type, members);

        var hud = enriched.Single(member => member.Name == "HudYOffsetScale");
        Assert.Equal(0.1, hud.NumericMin);
        Assert.Equal(3.5, hud.NumericMax);
        Assert.Equal("HUD offset factor", hud.Tooltip);

        var initialSkill = enriched.Single(member => member.Name == "InitialSkill");
        Assert.True(initialSkill.IsHiddenInInspector);
        Assert.True(initialSkill.IsSoundIdField);
    }

    [Fact]
    public void EnrichMembers_MatchesInheritedMembersByTheirDeclaringType()
    {
        // FixtureEntity inherits m_ID / m_Name from FixtureBaseEntity.
        // The supplement entries are keyed under the base type, so the
        // enrichment must fall back to the member's own declaring type.
        var supplement = new Il2CppMetadataSupplement
        {
            NamedArrays =
            [
                new NamedArrayPairing
                {
                    TemplateTypeShortName = "FixtureBaseEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureBaseEntity",
                    FieldName = "m_ID",
                    EnumTypeShortName = "FixtureAttribute",
                },
            ],
            Fields =
            [
                new FieldMetadata
                {
                    TemplateTypeShortName = "FixtureBaseEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureBaseEntity",
                    FieldName = "m_Name",
                    RangeMin = 0,
                    RangeMax = 128,
                    Tooltip = "Base entity name",
                },
            ],
        };

        using var catalog = LoadWithSupplement(supplement);
        var type = catalog.ResolveType("FixtureEntity", out _, out _)!;
        var members = TemplateTypeCatalog.GetMembers(type);

        var enriched = catalog.EnrichMembers(type, members);

        var id = enriched.Single(m => m.Name == "m_ID");
        Assert.True(id.IsInherited);
        Assert.Equal("FixtureAttribute", id.NamedArrayEnumTypeName);

        var name = enriched.Single(m => m.Name == "m_Name");
        Assert.True(name.IsInherited);
        Assert.Equal(0, name.NumericMin);
        Assert.Equal(128, name.NumericMax);
        Assert.Equal("Base entity name", name.Tooltip);
    }

    // --- HasReferenceSubtype: structural polymorphism check used by the
    // editor to decide whether to show the ref-type combobox. The Type.IsAbstract
    // bit is unreliable across the IL2CPP wrapper boundary, so we look for
    // any strict descendant that is itself a reference target.

    [Fact]
    public void HasReferenceSubtype_TrueWhenStrictRefDescendantExists()
    {
        // FixtureBaseDataTemplate has FixtureConcreteDerived as a subtype.
        // This mirrors the BaseItemTemplate / ModularVehicleWeaponTemplate
        // case that surfaced the editor bug.
        using var catalog = Load();
        var baseType = catalog.ResolveType("FixtureBaseDataTemplate", out _, out _)!;

        Assert.True(catalog.HasReferenceSubtype(baseType));
    }

    [Fact]
    public void HasReferenceSubtype_FalseForLeafTemplateWithoutDescendants()
    {
        // FixtureConcreteDerived is itself the leaf — no further descendants
        // in the fixture assembly.
        using var catalog = Load();
        var leaf = catalog.ResolveType(
            "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureConcreteDerived",
            out _, out _)!;

        Assert.False(catalog.HasReferenceSubtype(leaf));
    }
}
