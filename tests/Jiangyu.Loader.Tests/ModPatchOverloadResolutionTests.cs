using System.Reflection;
using Jiangyu.Loader.Sdk.Patches;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the parameter-count overload selection the patch API uses to bind a
/// specific overload of a game method. The motivating case was
/// Actor.OnDamageReceived, which has 3- and 4-argument forms, so a patch by name
/// alone throws AmbiguousMatchException and never binds.
/// </summary>
public class ModPatchOverloadResolutionTests
{
    private static class Sample
    {
        public static void Foo(int a) { }
        public static void Foo(int a, int b) { }
        public static void Foo(int a, int b, int c) { }
        public static void Bar() { }
        // Two overloads at the same arity: not resolvable by count alone.
        public static int Ambiguous(int a) => a;
        public static string Ambiguous(string a) => a;
    }

    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static MethodInfo[] Methods => typeof(Sample).GetMethods(Flags);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SelectsTheOverloadWithMatchingArity(int arity)
    {
        var method = ModPatchCoordinator.SelectMethodByArity(Methods, "Foo", arity);
        Assert.NotNull(method);
        Assert.Equal("Foo", method.Name);
        Assert.Equal(arity, method.GetParameters().Length);
    }

    [Fact]
    public void SelectsZeroArgMethod()
    {
        var method = ModPatchCoordinator.SelectMethodByArity(Methods, "Bar", 0);
        Assert.NotNull(method);
        Assert.Equal("Bar", method.Name);
    }

    [Fact]
    public void ReturnsNullWhenNoOverloadHasThatArity()
        => Assert.Null(ModPatchCoordinator.SelectMethodByArity(Methods, "Foo", 9));

    [Fact]
    public void ReturnsNullWhenNameDoesNotExist()
        => Assert.Null(ModPatchCoordinator.SelectMethodByArity(Methods, "Nope", 1));

    [Fact]
    public void ReturnsNullWhenTwoOverloadsShareThatArity()
        => Assert.Null(ModPatchCoordinator.SelectMethodByArity(Methods, "Ambiguous", 1));
}
