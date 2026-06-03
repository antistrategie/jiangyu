using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Jiangyu.Sdk.Analyzers.Tests;

public sealed class JiangyuTypeConstructorGeneratorTests
{
    // A stub for a game/IL2CPP base: a [JiangyuType] derives one of these, never object,
    // so the generated base(...) calls resolve. Prepended to every case.
    private const string GameBase = """
        using System;
        public class GameBase
        {
            public GameBase(IntPtr ptr) { }
            public GameBase() { }
        }
        namespace Il2CppInterop.Runtime.Injection
        {
            public static class ClassInjector
            {
                public static IntPtr DerivedConstructorPointer<T>() => IntPtr.Zero;
                public static void DerivedConstructorBody(object instance) { }
            }
        }
        """;

    private static GeneratorDriverRunResult Run(string source)
        => CSharpGeneratorDriver
            .Create(new JiangyuTypeConstructorGenerator().AsSourceGenerator())
            .RunGenerators(TestCompilation.Create(GameBase, source))
            .GetRunResult();

    private static string GeneratedText(GeneratorDriverRunResult result)
        => string.Join("\n", result.GeneratedTrees.Select(tree => tree.GetText().ToString()));

    [Fact]
    public void GeneratedConstructorsCompile()
    {
        var compilation = TestCompilation.Create(GameBase, """
            using Jiangyu.Sdk;
            namespace Mod
            {
                [JiangyuType("Focus")]
                public sealed partial class Focus : GameBase { }
            }
            """);

        var driver = CSharpGeneratorDriver
            .Create(new JiangyuTypeConstructorGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var run = driver.GetRunResult();
        Assert.All(run.Results, result => Assert.Null(result.Exception));
        Assert.NotEmpty(run.GeneratedTrees);
        Assert.Empty(updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void PartialType_WithBaseLackingPointerConstructor_GeneratesNothing()
    {
        // A plain managed base has no IntPtr constructor, so the generated base(IntPtr)
        // calls could not compile against it: emit nothing rather than broken code.
        var result = Run("""
            using Jiangyu.Sdk;
            public class PlainBase { }
            [JiangyuType] public sealed partial class Foo : PlainBase { }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void NestedType_GeneratesCompilablePartialContainers()
    {
        var compilation = TestCompilation.Create(GameBase, """
            using Jiangyu.Sdk;
            namespace Mod
            {
                public partial class Outer
                {
                    [JiangyuType("Inner")]
                    public sealed partial class Inner : GameBase { }
                }
            }
            """);

        var driver = CSharpGeneratorDriver
            .Create(new JiangyuTypeConstructorGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var run = driver.GetRunResult();
        Assert.NotEmpty(run.GeneratedTrees);
        Assert.Empty(updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void PartialType_WithNoConstructors_GeneratesBoth()
    {
        var text = GeneratedText(Run("""
            using Jiangyu.Sdk;
            namespace Mod
            {
                [JiangyuType("Focus")]
                public sealed partial class Focus : GameBase { }
            }
            """));

        Assert.Contains("namespace Mod", text);
        Assert.Contains("partial class Focus", text);
        Assert.Contains("public Focus(global::System.IntPtr pointer) : base(pointer) { }", text);
        Assert.Contains("public Focus()", text);
        Assert.Contains("global::Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<Focus>()", text);
        Assert.Contains("global::Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this)", text);
    }

    [Fact]
    public void PartialType_WithBothConstructors_GeneratesNothing()
    {
        var result = Run("""
            using System;
            using Jiangyu.Sdk;
            [JiangyuType] public sealed partial class Focus : GameBase
            {
                public Focus(IntPtr ptr) : base(ptr) { }
                public Focus() : base() { }
            }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void PartialType_WithOnlyPointerConstructor_GeneratesParameterlessOnly()
    {
        var text = GeneratedText(Run("""
            using System;
            using Jiangyu.Sdk;
            [JiangyuType] public sealed partial class Focus : GameBase
            {
                public Focus(IntPtr ptr) : base(ptr) { }
            }
            """));

        Assert.DoesNotContain("(global::System.IntPtr pointer)", text);
        Assert.Contains("public Focus()", text);
    }

    [Fact]
    public void NonPartialType_GeneratesNothing()
    {
        var result = Run("""
            using Jiangyu.Sdk;
            [JiangyuType] public class Focus : GameBase { }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void ObjectRootedType_GeneratesNothing()
    {
        // Not injectable (no game base), so emitting base(...) calls would not compile.
        var result = Run("""
            using Jiangyu.Sdk;
            [JiangyuType] public sealed partial class Focus { }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void PartialRecord_GeneratesNothing()
    {
        var result = Run("""
            using Jiangyu.Sdk;
            [JiangyuType] public sealed partial record Focus : GameBase { }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void PrimaryConstructorOnAnotherPartialPart_GeneratesNothing()
    {
        // The primary constructor sits on a different partial part than the attributed
        // one, so the syntax predicate (which sees only the attributed declaration's
        // empty parameter list) passes; the symbol-level check must still skip it, since
        // base(...) constructors can't be emitted onto a class with a primary constructor.
        var result = Run("""
            using Jiangyu.Sdk;
            [JiangyuType] public partial class Focus { }
            public partial class Focus(int x) : GameBase { }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void TypeNestedInGenericContainer_GeneratesNothing()
    {
        // Inner carries Outer's type parameter, so it is generic and the generator skips
        // it like any generic type rather than emitting an open-generic constructor.
        var result = Run("""
            using Jiangyu.Sdk;
            public partial class Outer<T>
            {
                [JiangyuType] public sealed partial class Inner : GameBase { }
            }
            """);

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void PrivateParameterlessConstructor_GeneratesNothing()
    {
        // The injector cannot call a private parameterless ctor and the generator cannot
        // add a second one, so the type is not injectable and nothing is emitted.
        var result = Run("""
            using System;
            using Jiangyu.Sdk;
            [JiangyuType] public sealed partial class Focus : GameBase
            {
                private Focus() { }
            }
            """);

        Assert.Empty(result.GeneratedTrees);
    }
}
