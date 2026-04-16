using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

public class StructuralBaselineServiceTests
{
    [Fact]
    public void ExtractFieldEntries_PreservesArrayElementType_ForEmptyArrays()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Loot",
                Kind = "array",
                FieldTypeName = "Array<EntityLootEntry>",
                Count = 0,
                Elements = null,
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(fields);

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Equal("EntityLootEntry", entry.ElementTypeName);
    }

    [Theory]
    [InlineData("Array<EntityLootEntry>", "EntityLootEntry")]
    [InlineData("Array< Menace.Tactical.EntityProperties >", "Menace.Tactical.EntityProperties")]
    [InlineData("System.Collections.Generic.List`1<Menace.Tactical.EntityLootEntry>", "Menace.Tactical.EntityLootEntry")]
    [InlineData("List<EntityLootEntry>", "EntityLootEntry")]
    [InlineData("String[]", "String")]
    [InlineData("Dictionary<string, int>", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void TryParseArrayElementTypeName_ParsesOnlyArrayTypeNames(string? input, string? expected)
    {
        string? actual = StructuralBaselineService.TryParseArrayElementTypeName(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExtractFieldEntries_SetsStorageReference_ForArrayWithReferenceElements()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Items",
                Kind = "array",
                FieldTypeName = "System.Collections.Generic.List`1<Menace.Items.ItemTemplate>",
                Count = 2,
                Elements =
                [
                    new() { Kind = "reference", FieldTypeName = "Menace.Items.ArmorTemplate" },
                ],
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(fields);

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Equal("reference", entry.Storage);
    }

    [Fact]
    public void ExtractFieldEntries_SetsStorageInline_ForArrayWithObjectElements()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "AttachedPrefabs",
                Kind = "array",
                FieldTypeName = "Menace.Tactical.PrefabAttachment[]",
                Count = 1,
                Elements =
                [
                    new() { Kind = "object", FieldTypeName = "Menace.Tactical.PrefabAttachment" },
                ],
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(fields);

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Equal("inline", entry.Storage);
    }

    [Fact]
    public void ExtractFieldEntries_SetsStorageInline_ForArrayWithScalarElements()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Scores",
                Kind = "array",
                FieldTypeName = "Array<Int32>",
                Count = 3,
                Elements =
                [
                    new() { Kind = "int", FieldTypeName = "Int32" },
                ],
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(fields);

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Equal("inline", entry.Storage);
    }

    [Fact]
    public void ExtractFieldEntries_SetsStorageNull_ForEmptyArray()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Tags",
                Kind = "array",
                FieldTypeName = "System.Collections.Generic.List`1<Menace.Tags.TagTemplate>",
                Count = 0,
                Elements = null,
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(fields);

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Null(entry.Storage);
    }

    [Fact]
    public void ExtractFieldEntries_InfersStorageReference_ForEmptyArray_WhenTypeMetadataSaysReference()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Tags",
                Kind = "array",
                FieldTypeName = "System.Collections.Generic.List`1<Menace.Tags.TagTemplate>",
                Count = 0,
                Elements = null,
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(
            fields,
            _ => "reference");

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Equal("reference", entry.Storage);
    }

    [Fact]
    public void ExtractFieldEntries_InfersStorageInline_ForEmptyArray_WhenTypeMetadataSaysInline()
    {
        var fields = new List<InspectedFieldNode>
        {
            new()
            {
                Name = "Loot",
                Kind = "array",
                FieldTypeName = "System.Collections.Generic.List`1<Menace.Tactical.EntityLootEntry>",
                Count = 0,
                Elements = null,
            },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(
            fields,
            _ => "inline");

        BaselineFieldEntry entry = Assert.Single(entries);
        Assert.Equal("inline", entry.Storage);
    }

    [Fact]
    public void ExtractFieldEntries_SetsStorageNull_ForNonArrayFields()
    {
        var fields = new List<InspectedFieldNode>
        {
            new() { Name = "Armor", Kind = "int", FieldTypeName = "Int32" },
            new() { Name = "Properties", Kind = "object", FieldTypeName = "Menace.Tactical.EntityProperties" },
            new() { Name = "Badge", Kind = "reference", FieldTypeName = "UnityEngine.Sprite" },
        };

        List<BaselineFieldEntry> entries = StructuralBaselineService.ExtractFieldEntries(fields);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Null(e.Storage));
    }

    [Fact]
    public void ResolveStorage_ReturnsReference_ForReferenceElement()
    {
        var field = new InspectedFieldNode
        {
            Name = "Skills",
            Kind = "array",
            Count = 1,
            Elements = [new() { Kind = "reference", FieldTypeName = "Menace.Tactical.Skills.SkillTemplate" }],
        };

        Assert.Equal("reference", StructuralBaselineService.ResolveStorage(field));
    }

    [Fact]
    public void ResolveStorage_ReturnsInline_ForObjectElement()
    {
        var field = new InspectedFieldNode
        {
            Name = "Loot",
            Kind = "array",
            Count = 1,
            Elements = [new() { Kind = "object", FieldTypeName = "Menace.Tactical.EntityLootEntry" }],
        };

        Assert.Equal("inline", StructuralBaselineService.ResolveStorage(field));
    }

    [Fact]
    public void ResolveStorage_ReturnsNull_ForNonArrayField()
    {
        var field = new InspectedFieldNode { Name = "Armor", Kind = "int" };

        Assert.Null(StructuralBaselineService.ResolveStorage(field));
    }

    [Fact]
    public void ResolveStorage_ReturnsNull_ForEmptyArray()
    {
        var field = new InspectedFieldNode
        {
            Name = "Tags",
            Kind = "array",
            Count = 0,
            Elements = null,
        };

        Assert.Null(StructuralBaselineService.ResolveStorage(field));
    }

    [Fact]
    public void ResolveStorage_UsesTypeInference_ForEmptyArray()
    {
        var field = new InspectedFieldNode
        {
            Name = "Tags",
            Kind = "array",
            FieldTypeName = "System.Collections.Generic.List`1<Menace.Tags.TagTemplate>",
            Count = 0,
            Elements = null,
        };

        Assert.Equal("reference", StructuralBaselineService.ResolveStorage(field, _ => "reference"));
    }

    [Theory]
    [InlineData("Menace.Tactical.PrefabAttachment", "PrefabAttachment", true)]
    [InlineData("PrefabAttachment", "Menace.Tactical.PrefabAttachment", true)]
    [InlineData("Menace.Tactical.EntityProperties", "Menace.Tactical.EntityProperties", true)]
    [InlineData("Menace.Tactical.EntityProperties", "Menace.Tactical.RoleData", false)]
    [InlineData(null, "PrefabAttachment", false)]
    public void MatchesTypeName_NormalizesQualifiedAndSimpleNames(string? observed, string? expected, bool matches)
    {
        bool actual = StructuralBaselineService.MatchesTypeName(observed, expected);

        Assert.Equal(matches, actual);
    }
}
