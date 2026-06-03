using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Jiangyu.Sdk.Analyzers.Tests;

public sealed class JiangyuTypeAnalyzerTests
{
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var withAnalyzers = TestCompilation.Create(source).WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new JiangyuTypeAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();

        return diagnostics
            .Where(d => d.Id.StartsWith("JIA", StringComparison.Ordinal))
            .ToImmutableArray();
    }

    private static async Task<string[]> IdsAsync(string source)
        => (await AnalyzeAsync(source)).Select(d => d.Id).ToArray();

    [Fact]
    public async Task ConcreteType_WithDefaultName_HasNoDiagnostics()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public class MyHandler { }
            public class NotAModType { }
            """);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task ConcreteType_WithExplicitName_HasNoDiagnostics()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType("Aura")] public class X { }
            """);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task AbstractType_ReportsConcreteClassRule()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public abstract class Base { }
            """);

        Assert.Equal(new[] { "JIA001" }, ids);
    }

    [Fact]
    public async Task StaticType_ReportsConcreteClassRule()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public static class Helpers { }
            """);

        Assert.Equal(new[] { "JIA001" }, ids);
    }

    [Fact]
    public async Task NameWithColon_ReportsInvalidNameRule()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType("Bad:Name")] public class X { }
            """);

        Assert.Equal(new[] { "JIA002" }, ids);
    }

    [Fact]
    public async Task EmptyName_ReportsInvalidNameRule()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType("")] public class X { }
            """);

        Assert.Equal(new[] { "JIA002" }, ids);
    }

    [Fact]
    public async Task DuplicateEffectiveName_ReportsCollisionForEachType()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType("Dup")] public class First { }
            [JiangyuType("Dup")] public class Second { }
            """);

        Assert.Equal(new[] { "JIA003", "JIA003" }, ids);
    }

    [Fact]
    public async Task DefaultNameMatchingAnotherClassName_Collides()
    {
        // First's effective name is its class name "Foo"; Second declares "Foo"
        // explicitly. Both resolve to ns:Foo.
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public class Foo { }
            [JiangyuType("Foo")] public class Second { }
            """);

        Assert.Equal(new[] { "JIA003", "JIA003" }, ids);
    }

    [Fact]
    public async Task DelegateField_ReportsFieldNotSerialisable()
    {
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            [JiangyuType] public class X { public Action OnFire; }
            """);

        Assert.Equal(new[] { "JIA004" }, ids);
    }

    [Fact]
    public async Task NativeIntField_ReportsFieldNotSerialisable()
    {
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            [JiangyuType] public class X { public IntPtr Handle; }
            """);

        Assert.Equal(new[] { "JIA004" }, ids);
    }

    [Fact]
    public async Task SerialisableFields_AreNotFlagged()
    {
        // The rule is deliberately conservative: primitives, strings, and
        // collections (including Dictionary, which Odin can serialise) are left
        // unflagged so the analyser never false-positives.
        var ids = await IdsAsync("""
            using System.Collections.Generic;
            using Jiangyu.Sdk;
            [JiangyuType] public class X
            {
                public int Count;
                public string Note;
                public List<int> Items;
                public Dictionary<string, int> Table;
            }
            """);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task Diagnostic_IsReportedOnTheAttribute_NotTheClassName()
    {
        const string source = """
            using Jiangyu.Sdk;
            [JiangyuType("")] public sealed class DeathDefying { }
            """;

        var diagnostic = Assert.Single(await AnalyzeAsync(source));
        var flagged = source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

        Assert.Contains("JiangyuType", flagged);
        Assert.DoesNotContain("class", flagged);
    }

    [Fact]
    public async Task NonPartialType_DerivingGameBase_MissingConstructors_ReportsInjectionRule()
    {
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public class MyHandler : GameBase { }
            """);

        Assert.Equal(new[] { "JIA005" }, ids);
    }

    [Fact]
    public async Task PartialType_DerivingGameBase_MissingConstructors_HasNoInjectionRule()
    {
        // The generator supplies the constructors for a partial class, so no warning.
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public partial class MyHandler : GameBase { }
            """);

        Assert.DoesNotContain("JIA005", ids);
    }

    [Fact]
    public async Task NonPartialType_DerivingGameBase_WithConstructors_HasNoInjectionRule()
    {
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public class MyHandler : GameBase
            {
                public MyHandler(IntPtr p) : base(p) { }
                public MyHandler() : base() { }
            }
            """);

        Assert.DoesNotContain("JIA005", ids);
    }

    [Fact]
    public async Task ObjectRootedType_MissingConstructors_HasNoInjectionRule()
    {
        // Rooted on object, so not injectable and not the generator's concern.
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public class MyHandler { }
            """);

        Assert.DoesNotContain("JIA005", ids);
    }

    [Fact]
    public async Task GenericType_DerivingGameBase_HasNoInjectionRule()
    {
        // The generator never emits for a generic type, so 'mark partial' would not help.
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public class MyHandler<T> : GameBase { }
            """);

        Assert.DoesNotContain("JIA005", ids);
    }

    [Fact]
    public async Task StructDerivingValueType_HasNoInjectionRule()
    {
        // A struct's base is ValueType (not object) but the generator cannot emit for
        // it, so no dead-end 'mark partial' warning.
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public struct Foo { }
            """);

        Assert.DoesNotContain("JIA005", ids);
    }

    [Fact]
    public async Task GenericType_SharingNameWithConcreteType_DoesNotCollide()
    {
        // The loader drops the generic before its collision check, so the concrete
        // 'Widget' stands alone and there is no ns:Widget collision to report.
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public class Widget<T> { }
            [JiangyuType] public class Widget { }
            """);

        Assert.DoesNotContain("JIA003", ids);
    }

    [Fact]
    public async Task RecordDerivingGameBase_ReportsCannotBeInjected()
    {
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public partial record MyHandler : GameBase { }
            """);

        Assert.Equal(new[] { "JIA006" }, ids);
    }

    [Fact]
    public async Task PrimaryConstructorDerivingGameBase_ReportsCannotBeInjected()
    {
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public partial class MyHandler(int x) : GameBase { }
            """);

        Assert.Equal(new[] { "JIA006" }, ids);
    }

    [Fact]
    public async Task PrivateInjectionConstructor_ReportsCannotBeInjected()
    {
        // A private parameterless ctor can neither be called by the injector nor
        // replaced by the generator, so 'mark partial' would not help.
        var ids = await IdsAsync("""
            using System;
            using Jiangyu.Sdk;
            public class GameBase { public GameBase(IntPtr p) { } public GameBase() { } }
            [JiangyuType] public partial class MyHandler : GameBase
            {
                private MyHandler() { }
            }
            """);

        Assert.Equal(new[] { "JIA006" }, ids);
    }
}
