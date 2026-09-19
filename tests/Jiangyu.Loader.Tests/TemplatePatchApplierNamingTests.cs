using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class TemplatePatchApplierNamingTests
{
    [Theory]
    [InlineData("Il2CppMenace.Tactical.Skills.Effects.Attack", "Attack")]
    [InlineData("Attack", "Attack")]
    [InlineData("mymod:Handler", "mymod:Handler")]
    [InlineData("com.example.mod:Handler", "com.example.mod:Handler")]
    [InlineData("mymod:My.Handler", "mymod:My.Handler")]
    public void ObjectNameFor_KeepsEverySpellingButAFullName(string compiled, string expected)
    {
        Assert.Equal(expected, TemplatePatchApplier.ObjectNameFor(compiled, typeof(Attack)));
    }

    [Fact]
    public void ObjectNameFor_NamesAFullNameAfterTheLiveTypeNotTheSpelling()
    {
        // The constructed object is what gets named, so the type it turned out to be wins
        // over the last segment of the compiled spelling.
        Assert.Equal("Attack", TemplatePatchApplier.ObjectNameFor("Il2CppMenace.Tactical.Skills.Effects.Strike", typeof(Attack)));
    }

    private sealed class Attack { }
}
