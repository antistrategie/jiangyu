using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

public class TemplateIdentityIndexTests
{
    private sealed record Template(string? Name, string? Path);

    private static Dictionary<string, Template> Index(params Template[] templates)
        => TemplateCloneApplier.BuildIdentityIndex(templates, t => t.Name!, t => t.Path!);

    [Fact]
    public void ObjectNameTakesPrecedenceOverAnEarlierAlternateIdentity()
    {
        var earlier = new Template("click_bark", "Doll/click_bark");
        var named = new Template("Doll/click_bark", "Other/click_bark");
        var index = Index(earlier, named);

        Assert.Same(named, index["Doll/click_bark"]);
        Assert.Same(earlier, index["click_bark"]);
        Assert.Same(named, index["Other/click_bark"]);
    }

    [Fact]
    public void DuplicateNamesAndIdentitiesKeepTheirFirstMatch()
    {
        var first = new Template("click_bark", "Doll/click_bark");
        var duplicate = new Template("click_bark", "Doll/click_bark");
        var index = Index(first, duplicate);

        Assert.Same(first, index["click_bark"]);
        Assert.Same(first, index["Doll/click_bark"]);
        Assert.False(index.ContainsKey("doll/click_bark"));
    }

    [Fact]
    public void MissingNamesAndIdentitiesDoNotHideValidAliases()
    {
        var unnamed = new Template(null, "Doll/click_bark");
        var noIdentity = new Template("bank", null);
        var index = Index(null!, unnamed, noIdentity);

        Assert.Equal(2, index.Count);
        Assert.Same(unnamed, index["Doll/click_bark"]);
        Assert.Same(noIdentity, index["bank"]);
    }

    [Fact]
    public void LookupsReadEachNativeIdentityOnlyOnceAndNextPassSeesNewTemplates()
    {
        var templates = new List<Template> { new("click_bark", "Doll/click_bark") };
        var reads = 0;
        var index = TemplateCloneApplier.BuildIdentityIndex(templates,
            t => { reads++; return t.Name!; }, t => { reads++; return t.Path!; });
        for (var i = 0; i < 500; i++)
        {
            Assert.True(index.ContainsKey("Doll/click_bark"));
            Assert.False(index.ContainsKey("Late/click_bark"));
        }
        Assert.Equal(2, reads);

        var late = new Template("late", "Late/click_bark");
        templates.Add(late);
        Assert.Same(late, Index([.. templates])["Late/click_bark"]);
    }
}
