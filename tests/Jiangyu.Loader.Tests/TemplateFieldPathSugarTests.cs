using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

public class TemplateFieldPathSugarTests
{
    [Theory]
    [InlineData("Agility", 0)]
    [InlineData("WeaponSkill", 1)]
    [InlineData("Valour", 2)]
    [InlineData("Toughness", 3)]
    [InlineData("Vitality", 4)]
    [InlineData("Precision", 5)]
    [InlineData("Positioning", 6)]
    public void Rewrites_UnitLeader_InitialAttributes_ByName(string name, int expectedOffset)
    {
        var result = TemplateFieldPathSugar.Rewrite("UnitLeaderTemplate", $"InitialAttributes.{name}");

        Assert.True(result.Rewritten);
        Assert.Null(result.Error);
        Assert.Equal($"InitialAttributes[{expectedOffset}]", result.Path);
    }

    [Fact]
    public void Rewrite_UnknownAttribute_ReturnsErrorListingValidNames()
    {
        var result = TemplateFieldPathSugar.Rewrite("UnitLeaderTemplate", "InitialAttributes.NopeNotAThing");

        Assert.False(result.Rewritten);
        Assert.NotNull(result.Error);
        Assert.Contains("NopeNotAThing", result.Error);
        Assert.Contains("Agility", result.Error);
        Assert.Contains("Positioning", result.Error);
    }

    [Fact]
    public void Rewrite_WrongTemplateType_LeavesPathUnchanged()
    {
        var result = TemplateFieldPathSugar.Rewrite("EntityTemplate", "InitialAttributes.Agility");

        Assert.False(result.Rewritten);
        Assert.Null(result.Error);
        Assert.Equal("InitialAttributes.Agility", result.Path);
    }

    [Fact]
    public void Rewrite_PathWithoutSugarPrefix_LeavesPathUnchanged()
    {
        var result = TemplateFieldPathSugar.Rewrite("UnitLeaderTemplate", "Properties.Accuracy");

        Assert.False(result.Rewritten);
        Assert.Null(result.Error);
        Assert.Equal("Properties.Accuracy", result.Path);
    }

    [Fact]
    public void Rewrite_AlreadyIndexed_LeavesPathUnchanged()
    {
        var result = TemplateFieldPathSugar.Rewrite("UnitLeaderTemplate", "InitialAttributes[3]");

        Assert.False(result.Rewritten);
        Assert.Null(result.Error);
        Assert.Equal("InitialAttributes[3]", result.Path);
    }

    [Fact]
    public void Rewrite_NullInputs_AreSafe()
    {
        var result = TemplateFieldPathSugar.Rewrite(null, null);

        Assert.False(result.Rewritten);
        Assert.Null(result.Error);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Rewrite_AttributeWithTail_PreservesTail()
    {
        // Not a useful path (InitialAttributes[N] is a leaf byte), but the
        // rewriter should still preserve the tail so validation can reject
        // it cleanly rather than the sugar silently dropping segments.
        var result = TemplateFieldPathSugar.Rewrite("UnitLeaderTemplate", "InitialAttributes.Agility.Something");

        Assert.True(result.Rewritten);
        Assert.Null(result.Error);
        Assert.Equal("InitialAttributes[0].Something", result.Path);
    }

    [Fact]
    public void Rewrite_IsCaseSensitive()
    {
        // Enum-derived attribute names are PascalCase; lowercase should fail
        // loudly rather than be rewritten silently.
        var result = TemplateFieldPathSugar.Rewrite("UnitLeaderTemplate", "InitialAttributes.agility");

        Assert.False(result.Rewritten);
        Assert.NotNull(result.Error);
        Assert.Contains("agility", result.Error);
    }
}
