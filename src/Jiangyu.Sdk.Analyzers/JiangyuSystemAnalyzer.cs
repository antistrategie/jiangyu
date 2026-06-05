using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Jiangyu.Sdk.Analyzers;

/// <summary>
/// Surfaces, at author time, the JiangyuSystem and [DependsOn] mistakes the loader
/// only reports in a runtime log or silently swallows: a [DependsOn] target that is
/// not a sibling system, a self-dependency, a dependency cycle, and a system the
/// loader cannot construct (so it never runs). Each rule mirrors a loader behaviour,
/// so a flagged declaration is one whose ordering does not apply or which never runs.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JiangyuSystemAnalyzer : DiagnosticAnalyzer
{
    private const string SystemMetadataName = "Jiangyu.Sdk.JiangyuSystem";
    private const string DependsOnMetadataName = "Jiangyu.Sdk.DependsOnAttribute";

    private static readonly DiagnosticDescriptor InvalidDependency = new(
        id: "JIA010",
        title: "[DependsOn] target is not a sibling system",
        messageFormat: "[DependsOn] target '{0}' {1}, so the loader ignores that dependency and the ordering you expect does not apply",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "[DependsOn] orders a system after the sibling systems it names. A target that is not a JiangyuSystem, or the declaring system itself, cannot be ordered against, so the loader ignores it with a runtime warning.");

    private static readonly DiagnosticDescriptor DependencyCycle = new(
        id: "JIA011",
        title: "[DependsOn] cycle",
        messageFormat: "System '{0}' is in a [DependsOn] cycle ({1}); the loader breaks it by running the members in name order, so the declared order does not hold",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "[DependsOn] must form a directed acyclic graph. A cycle has no valid ordering, so the loader falls back to running the cycle's members in name order and the declared dependencies do not take effect.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor NotConstructable = new(
        id: "JIA012",
        title: "JiangyuSystem needs a public parameterless constructor",
        messageFormat: "JiangyuSystem '{0}' has no public parameterless constructor, so the loader cannot construct it and silently skips it: it never runs",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The loader discovers each concrete JiangyuSystem and instantiates it with a public parameterless constructor. A system that declares only parameterised constructors is never discovered and never runs, with no error. Add a parameterless constructor, or make a base meant only to be subclassed abstract.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InvalidDependency, DependencyCycle, NotConstructable);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var systemType = start.Compilation.GetTypeByMetadataName(SystemMetadataName);
            if (systemType is null)
                return;
            var dependsOn = start.Compilation.GetTypeByMetadataName(DependsOnMetadataName);

            // system -> the sibling systems it depends on, accumulated for cycle detection.
            var edges = new ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.TypeKind != TypeKind.Class || !DerivesFrom(type, systemType))
                    return;

                AnalyzeConstructable(symbolContext, type);
                if (dependsOn is not null)
                    AnalyzeDependencies(symbolContext, type, systemType, dependsOn, edges);
            }, SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end => ReportCycles(end, edges));
        });
    }

    // JIA012: a concrete system the loader cannot construct (no public parameterless ctor).
    private static void AnalyzeConstructable(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsStatic)
            return; // never discovered as a system, so not this rule's concern.

        var ctors = type.InstanceConstructors;
        var hasPublicParameterless = ctors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        if (!hasPublicParameterless)
            context.ReportDiagnostic(Diagnostic.Create(NotConstructable, type.Locations[0], type.Name));
    }

    // JIA010: each [DependsOn] target must be another JiangyuSystem, not this type. Valid
    // targets are recorded as edges so the compilation-end pass can spot cycles (JIA011).
    private static void AnalyzeDependencies(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        INamedTypeSymbol systemType,
        INamedTypeSymbol dependsOn,
        ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> edges)
    {
        var attribute = type.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, dependsOn));
        if (attribute is null || attribute.ConstructorArguments.Length == 0
            || attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
            return;

        var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
            ?? type.Locations[0];

        var deps = new List<INamedTypeSymbol>();
        foreach (var element in attribute.ConstructorArguments[0].Values)
        {
            if (element.Value is not INamedTypeSymbol dep)
                continue; // a null entry is reported by the loader at runtime, not here.

            if (SymbolEqualityComparer.Default.Equals(dep, type))
                context.ReportDiagnostic(Diagnostic.Create(InvalidDependency, location, dep.Name, "is the declaring system itself (a self-dependency)"));
            else if (!DerivesFrom(dep, systemType))
                context.ReportDiagnostic(Diagnostic.Create(InvalidDependency, location, dep.Name, "is not a JiangyuSystem"));
            else
                deps.Add(dep);
        }

        if (deps.Count > 0)
            edges[type] = deps.ToImmutableArray();
    }

    // JIA011: report every system that sits on a [DependsOn] cycle (a depth-first back edge).
    private static void ReportCycles(
        CompilationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> edges)
    {
        var state = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default); // 0 unseen, 1 on-stack, 2 done
        var stack = new List<INamedTypeSymbol>();
        var inCycle = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        void Visit(INamedTypeSymbol node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var dep in edges.TryGetValue(node, out var d) ? d : ImmutableArray<INamedTypeSymbol>.Empty)
            {
                state.TryGetValue(dep, out var s);
                if (s == 1)
                {
                    var from = stack.Count - 1;
                    while (from >= 0 && !SymbolEqualityComparer.Default.Equals(stack[from], dep))
                        from--;
                    for (var i = from; i >= 0 && i < stack.Count; i++)
                        inCycle.Add(stack[i]);
                }
                else if (s == 0 && edges.ContainsKey(dep))
                {
                    Visit(dep);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var node in edges.Keys)
            if (!state.ContainsKey(node))
                Visit(node);

        if (inCycle.Count == 0)
            return;

        var members = string.Join(" -> ", inCycle.Select(t => t.Name).OrderBy(n => n));
        foreach (var node in inCycle)
            context.ReportDiagnostic(Diagnostic.Create(DependencyCycle, node.Locations[0], node.Name, members));
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
                return true;
        return false;
    }
}
