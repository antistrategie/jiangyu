using System.Text.Json;
using Jiangyu.Core.Models;

namespace Jiangyu.Studio.Host.Tests;

public class TemplateSuggestionsTests
{
    private static TemplateIndex Index() => new()
    {
        Classification = new() { RuleVersion = "test", RuleDescription = "test" },
        TemplateTypes = [],
        Instances =
        [
            Instance("SkillTemplate", "active.Beta"),
            Instance("PerkTemplate", "passive.alpha"),
            Instance("SkillTemplate", "active.alpha"),
            Instance("SkillTemplate", "active.Beta"),
        ],
    };

    private static TemplateInstanceEntry Instance(string type, string name) => new()
    {
        ClassName = type,
        Name = name,
        Identity = new() { Collection = "resources", PathId = 1 },
        References = [new() { FieldName = "SkillsGranted", Target = new() { Collection = "resources", PathId = 2 } }],
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TypePicker_ReturnsDistinctIndexedTypeNames(string? className)
    {
        var result = RpcHandlers.BuildTemplateSuggestions(Index(), className);
        Assert.Equal(["PerkTemplate", "SkillTemplate"], result.Suggestions);
    }

    [Theory]
    [InlineData("SkillTemplate")]
    [InlineData("skilltemplate")]
    public void InstancePicker_FiltersAndDeduplicatesIdentifiers(string className)
    {
        var result = RpcHandlers.BuildTemplateSuggestions(Index(), className);
        Assert.Equal(["active.alpha", "active.Beta"], result.Suggestions);

        var response = JsonSerializer.SerializeToElement(result);
        Assert.Equal("suggestions", Assert.Single(response.EnumerateObject()).Name);
        Assert.All(response.GetProperty("suggestions").EnumerateArray(), value => Assert.Equal(JsonValueKind.String, value.ValueKind));
    }

    [Fact]
    public void UnknownType_ReturnsNoSuggestions()
    {
        Assert.Empty(RpcHandlers.BuildTemplateSuggestions(Index(), "Unknown").Suggestions);
    }
}
