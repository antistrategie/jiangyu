using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Jiangyu.Sdk.Analyzers.Tests;

public sealed class JiangyuUsageAnalyzerTests
{
    private static async Task<string[]> IdsAsync(string source)
    {
        var withAnalyzers = TestCompilation.Create(source).WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new JiangyuUsageAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id.StartsWith("JIA", StringComparison.Ordinal))
            .Select(d => d.Id)
            .ToArray();
    }

    // A fake mutating-verb surface tagged with [Jiangyu.Sdk.MutatingVerb] exactly like
    // the real one, so the verb rule is testable by attribute (not by name or type)
    // without the IL2CPP proxies.
    private const string FakeUnits = """
        namespace Jiangyu.Game
        {
            public static class Units
            {
                [Jiangyu.Sdk.MutatingVerb] public static int Spawn(object template, int faction, object tile) => 0;
                [Jiangyu.Sdk.MutatingVerb] public static bool Despawn(object actor, bool quiet = true) => true;
            }
        }
        """;

    // --- JIA007: base call from a [JiangyuType] override ---

    [Fact]
    public async Task BaseCall_FromJiangyuTypeOverride_ReportsJIA007()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class GameBase { public virtual void OnUpdate() { } }
            [JiangyuType] public class H : GameBase { public override void OnUpdate() { base.OnUpdate(); } }
            """);

        Assert.Equal(new[] { "JIA007" }, ids);
    }

    [Fact]
    public async Task BaseCall_FromPlainClass_HasNoDiagnostic()
    {
        var ids = await IdsAsync("""
            public class GameBase { public virtual void OnUpdate() { } }
            public class H : GameBase { public override void OnUpdate() { base.OnUpdate(); } }
            """);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task NonBaseCall_FromJiangyuType_HasNoDiagnostic()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [JiangyuType] public class H { void Helper() { } void Run() { Helper(); this.Helper(); } }
            """);

        Assert.Empty(ids);
    }

    // --- JIA008: Subscribe in a repeated lifecycle method ---

    [Fact]
    public async Task Subscribe_InOnSceneLoaded_ReportsJIA008()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class M : JiangyuMod
            {
                public override void OnSceneLoaded(int b, string s) { Context.Hooks.Subscribe<object>(_ => { }); }
            }
            """);

        Assert.Equal(new[] { "JIA008" }, ids);
    }

    [Fact]
    public async Task Subscribe_InOnUpdate_ReportsJIA008()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class M : JiangyuMod
            {
                public override void OnUpdate() { Context.Hooks.Subscribe<object>(_ => { }); }
            }
            """);

        Assert.Equal(new[] { "JIA008" }, ids);
    }

    [Fact]
    public async Task Subscribe_InOnInit_HasNoDiagnostic()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class M : JiangyuMod
            {
                public override void OnInit() { Context.Hooks.Subscribe<object>(_ => { }); }
            }
            """);

        Assert.Empty(ids);
    }

    // --- JIA009: mutating verb in an unsafe override ---

    [Fact]
    public async Task SpawnVerb_InPredicateOverride_ReportsJIA009()
    {
        var ids = await IdsAsync($$"""
            using Jiangyu.Sdk;
            using Jiangyu.Game;
            public class CondBase { public virtual bool IsTrue() => true; }
            [JiangyuType] public class C : CondBase
            {
                public override bool IsTrue() { Units.Spawn(null, 0, null); return true; }
            }
            {{FakeUnits}}
            """);

        Assert.Equal(new[] { "JIA009" }, ids);
    }

    [Fact]
    public async Task DespawnVerb_InPollOverride_ReportsJIA009()
    {
        var ids = await IdsAsync($$"""
            using Jiangyu.Sdk;
            using Jiangyu.Game;
            public class HandlerBase { public virtual void OnTurnStart() { } }
            [JiangyuType] public class H : HandlerBase
            {
                public override void OnTurnStart() { Units.Despawn(null); }
            }
            {{FakeUnits}}
            """);

        Assert.Equal(new[] { "JIA009" }, ids);
    }

    // Pins the third unsafe-context name (OnUpdate poll) so it cannot be dropped from
    // UnsafeOverrideAdvice without a test failing.
    [Fact]
    public async Task SpawnVerb_InOnUpdatePollOverride_ReportsJIA009()
    {
        var ids = await IdsAsync($$"""
            using Jiangyu.Sdk;
            using Jiangyu.Game;
            public class HandlerBase { public virtual void OnUpdate() { } }
            [JiangyuType] public class H : HandlerBase
            {
                public override void OnUpdate() { Units.Spawn(null, 0, null); }
            }
            {{FakeUnits}}
            """);

        Assert.Equal(new[] { "JIA009" }, ids);
    }

    [Fact]
    public async Task SpawnVerb_InOrdinaryMethod_HasNoDiagnostic()
    {
        var ids = await IdsAsync($$"""
            using Jiangyu.Game;
            public class C { public void DoThing() { Units.Spawn(null, 0, null); } }
            {{FakeUnits}}
            """);

        Assert.Empty(ids);
    }

    // A verb inside a lambda/local function within a poll override runs at the
    // delegate's cadence (here deferred), not the override's, so it must not warn.
    [Fact]
    public async Task MutatingVerb_InsideLambdaInPollOverride_HasNoDiagnostic()
    {
        var ids = await IdsAsync($$"""
            using System;
            using Jiangyu.Sdk;
            using Jiangyu.Game;
            public class HandlerBase { public virtual void OnTurnStart() { } }
            [JiangyuType] public class H : HandlerBase
            {
                public override void OnTurnStart() { Action a = () => Units.Spawn(null, 0, null); a(); }
            }
            {{FakeUnits}}
            """);

        Assert.Empty(ids);
    }
}
