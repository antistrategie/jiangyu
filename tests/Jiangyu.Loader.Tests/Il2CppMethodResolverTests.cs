using Jiangyu.Loader.Runtime.Patching;
using Xunit;

namespace Jiangyu.Loader.Tests;

// StripGenericArity is load-bearing: the loader's guard patches resolve game methods by matching the
// declaring type's name, and a generic IL2CPP type surfaces as "Name`N". Getting the strip wrong would
// silently fail every method resolution that routes through Il2CppMethodResolver.
public sealed class Il2CppMethodResolverTests
{
    [Theory]
    [InlineData("List`1", "List")]
    [InlineData("Dictionary`2", "Dictionary")]
    [InlineData("SuppressionHandler", "SuppressionHandler")]
    [InlineData("ModularVehicleSystem", "ModularVehicleSystem")]
    [InlineData("", "")]
    public void StripGenericArity_RemovesBacktickArityMarker(string input, string expected)
    {
        Assert.Equal(expected, Il2CppMethodResolver.StripGenericArity(input));
    }

    [Fact]
    public void StripGenericArity_NullPassesThrough()
    {
        Assert.Null(Il2CppMethodResolver.StripGenericArity(null));
    }

    [Fact]
    public void StripGenericArity_StripsAtFirstBacktick()
    {
        Assert.Equal("Outer", Il2CppMethodResolver.StripGenericArity("Outer`1`2"));
    }
}
