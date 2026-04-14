using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public class TemplateClassifierTests
{
    [Fact]
    public void IsTemplateLike_MatchesTemplateSuffixOnly()
    {
        Assert.True(TemplateClassifier.IsTemplateLike("EntityTemplate"));
        Assert.True(TemplateClassifier.IsTemplateLike("WeaponTemplate"));
        Assert.False(TemplateClassifier.IsTemplateLike("LocaState"));
        Assert.False(TemplateClassifier.IsTemplateLike("entitytemplate"));
        Assert.False(TemplateClassifier.IsTemplateLike(null));
    }

    [Fact]
    public void GetMetadata_ReturnsVersionedRuleDescription()
    {
        var metadata = TemplateClassifier.GetMetadata();

        Assert.Equal("v2", metadata.RuleVersion);
        Assert.Contains("MonoBehaviour", metadata.RuleDescription);
        Assert.Contains("m_Script", metadata.RuleDescription);
    }
}
