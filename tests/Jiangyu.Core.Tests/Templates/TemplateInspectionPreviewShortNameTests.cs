using Jiangyu.Core.Templates;
using Xunit;

namespace Jiangyu.Core.Tests.Templates;

public sealed class TemplateInspectionPreviewShortNameTests
{
    [Theory]
    [InlineData("Il2CppMenace.Tactical.Skills.Effects.IgnoreDamage", "IgnoreDamage")]
    [InlineData("IgnoreDamage", "IgnoreDamage")]
    [InlineData("Il2CppMenace.Tools.Outer+Inner", "Inner")]
    [InlineData("Il2CppMenace.Tools.Outer`1+Inner", "Inner")]
    [InlineData("mymod:My.Handler", "mymod:My.Handler")]
    [InlineData("Il2CppSystem.Collections.Generic.List`1[[Il2CppMenace.Tools.DataTemplate, Assembly-CSharp]]", "Il2CppSystem.Collections.Generic.List`1[[Il2CppMenace.Tools.DataTemplate, Assembly-CSharp]]")]
    public void ShortTypeName_ShowsTheTypeAsThePreviewAlwaysDid(string compiled, string shown)
    {
        Assert.Equal(shown, TemplateInspectionPreviewApplier.ShortTypeName(compiled));
    }

    [Fact]
    public void ShortTypeName_LeavesNothingAsNothing()
    {
        Assert.Null(TemplateInspectionPreviewApplier.ShortTypeName(null));
    }
}
