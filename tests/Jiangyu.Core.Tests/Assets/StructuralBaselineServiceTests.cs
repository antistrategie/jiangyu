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
