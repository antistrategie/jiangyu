using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Models;

public sealed class TemplateIndexFilterTests
{
    private static TemplateInstanceEntry Instance(string collection, long pathId, string className = "EntityTemplate", string name = "x") =>
        new()
        {
            Name = name,
            ClassName = className,
            Identity = new TemplateIdentity { Collection = collection, PathId = pathId },
        };

    private static TemplateReferenceEntry Ref(string collection, long pathId, string fieldName = "Field") =>
        new()
        {
            Source = new TemplateIdentity { Collection = collection, PathId = pathId },
            FieldName = fieldName,
        };

    [Fact]
    public void FilterReferencedBy_ReturnsNull_WhenInputIsNull()
    {
        var result = TemplateIndex.FilterReferencedBy(null, new List<TemplateInstanceEntry>());
        Assert.Null(result);
    }

    [Fact]
    public void FilterReferencedBy_ReturnsInput_WhenInputIsEmpty()
    {
        var empty = new Dictionary<string, List<TemplateReferenceEntry>>();
        var result = TemplateIndex.FilterReferencedBy(empty, new List<TemplateInstanceEntry>());
        Assert.Same(empty, result);
    }

    [Fact]
    public void FilterReferencedBy_DropsSourcesNotInVisibleList()
    {
        var referencedBy = new Dictionary<string, List<TemplateReferenceEntry>>
        {
            ["level0:100"] = [Ref("level0", 1), Ref("level0", 2)],
        };
        var visible = new List<TemplateInstanceEntry> { Instance("level0", 1) };

        var result = TemplateIndex.FilterReferencedBy(referencedBy, visible);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result["level0:100"]);
        Assert.Equal(1, result["level0:100"][0].Source.PathId);
    }

    [Fact]
    public void FilterReferencedBy_DropsTargetsWithNoSurvivingSources()
    {
        var referencedBy = new Dictionary<string, List<TemplateReferenceEntry>>
        {
            ["level0:100"] = [Ref("level0", 1)],
            ["level0:200"] = [Ref("level0", 99)], // no visible source
        };
        var visible = new List<TemplateInstanceEntry> { Instance("level0", 1) };

        var result = TemplateIndex.FilterReferencedBy(referencedBy, visible);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.True(result.ContainsKey("level0:100"));
        Assert.False(result.ContainsKey("level0:200"));
    }

    [Fact]
    public void FilterReferencedBy_ReturnsNull_WhenAllTargetsFiltered()
    {
        var referencedBy = new Dictionary<string, List<TemplateReferenceEntry>>
        {
            ["level0:200"] = [Ref("level0", 99)],
        };
        var visible = new List<TemplateInstanceEntry> { Instance("level0", 1) };

        var result = TemplateIndex.FilterReferencedBy(referencedBy, visible);

        Assert.Null(result);
    }

    [Fact]
    public void FilterReferencedBy_MatchesByCollectionAndPathId()
    {
        // Same pathId but different collections must not match each other.
        var referencedBy = new Dictionary<string, List<TemplateReferenceEntry>>
        {
            ["target:1"] = [Ref("resources.assets", 5), Ref("level0", 5)],
        };
        var visible = new List<TemplateInstanceEntry> { Instance("level0", 5) };

        var result = TemplateIndex.FilterReferencedBy(referencedBy, visible);

        Assert.NotNull(result);
        Assert.Single(result["target:1"]);
        Assert.Equal("level0", result["target:1"][0].Source.Collection);
    }
}
