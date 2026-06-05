using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Jiangyu.Sdk.Analyzers.Tests;

public sealed class JiangyuSystemAnalyzerTests
{
    private static async Task<string[]> IdsAsync(string source)
    {
        var withAnalyzers = TestCompilation.Create(source).WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new JiangyuSystemAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id.StartsWith("JIA", StringComparison.Ordinal))
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    // --- JIA010: invalid [DependsOn] target ---

    [Fact]
    public async Task SelfDependency_ReportsJIA010()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [DependsOn(typeof(S))] public class S : JiangyuSystem { }
            """);

        Assert.Equal(new[] { "JIA010" }, ids);
    }

    [Fact]
    public async Task NonSystemDependency_ReportsJIA010()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [DependsOn(typeof(string))] public class S : JiangyuSystem { }
            """);

        Assert.Equal(new[] { "JIA010" }, ids);
    }

    [Fact]
    public async Task ValidDependency_HasNoDiagnostic()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class A : JiangyuSystem { }
            [DependsOn(typeof(A))] public class B : JiangyuSystem { }
            """);

        Assert.Empty(ids);
    }

    // --- JIA011: dependency cycle ---

    [Fact]
    public async Task Cycle_ReportsJIA011OnEachMember()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            [DependsOn(typeof(B))] public class A : JiangyuSystem { }
            [DependsOn(typeof(A))] public class B : JiangyuSystem { }
            """);

        Assert.Equal(new[] { "JIA011", "JIA011" }, ids);
    }

    // --- JIA012: system the loader cannot construct ---

    [Fact]
    public async Task MissingParameterlessConstructor_ReportsJIA012()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class S : JiangyuSystem { public S(int x) { } }
            """);

        Assert.Equal(new[] { "JIA012" }, ids);
    }

    [Fact]
    public async Task PublicParameterlessConstructor_HasNoDiagnostic()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public class S : JiangyuSystem { public S() { } public S(int x) { } }
            """);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task AbstractBaseWithoutParameterlessConstructor_HasNoJIA012()
    {
        var ids = await IdsAsync("""
            using Jiangyu.Sdk;
            public abstract class S : JiangyuSystem { protected S(int x) { } }
            """);

        Assert.Empty(ids);
    }
}
