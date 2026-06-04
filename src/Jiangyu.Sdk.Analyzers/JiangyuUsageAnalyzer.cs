using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Jiangyu.Sdk.Analyzers;

/// <summary>
/// Surfaces, at author time, the runtime gotchas of using the SDK from code: a
/// <c>base.*</c> call out of a <c>[JiangyuType]</c> override (a native crash the
/// runtime cannot guard), a hook <c>Subscribe</c> in a per-scene/per-frame
/// lifecycle method (stacks duplicate handlers), and a state-mutating game verb in
/// a context where it is unsafe or refused (a predicate or polling override). Each
/// rule mirrors a documented runtime hazard so a flagged call is one that misbehaves
/// at runtime.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JiangyuUsageAnalyzer : DiagnosticAnalyzer
{
    private const string JiangyuTypeAttributeMetadataName = "Jiangyu.Sdk.JiangyuTypeAttribute";
    private const string HookBusMetadataName = "Jiangyu.Sdk.IHookBus";
    private const string MutatingVerbAttributeMetadataName = "Jiangyu.Sdk.MutatingVerbAttribute";

    private static readonly DiagnosticDescriptor BaseCallFromInjected = new(
        id: "JIA007",
        title: "[JiangyuType] override must not call base",
        messageFormat: "Do not call 'base.{0}' from a [JiangyuType] override; a base call on an injected type re-dispatches into the override and overflows the native stack with no managed trace. Make the override self-contained.",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An injected [JiangyuType] overrides a game virtual. Calling base from that override re-enters the injected vtable slot and recurses to a native stack overflow. Overrides on injected types must not call base.");

    private static readonly DiagnosticDescriptor SubscribeOutsideOnInit = new(
        id: "JIA008",
        title: "Hook subscription in a repeated lifecycle method",
        messageFormat: "Hooks.Subscribe in '{0}' re-subscribes on every {1}, stacking duplicate handlers that all fire. Subscribe once in OnInit.",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Hook subscriptions live for the mod's lifetime and the bus does not de-duplicate. Subscribing from OnSceneLoaded or OnUpdate adds a new handler each time the method runs, so the same hook fires many times. Subscribe in OnInit.");

    private static readonly DiagnosticDescriptor MutatingVerbInUnsafeContext = new(
        id: "JIA009",
        title: "State-mutating verb in an unsafe override",
        messageFormat: "'{0}' mutates game state inside '{1}'; {2}",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A predicate override (e.g. a condition's IsTrue) runs during evaluation, where a mutating verb is refused at runtime to avoid corrupting the decision pass. A polling override (OnUpdate/OnTurnStart) fires repeatedly, so an unguarded mutation compounds and re-fires on reload. Trigger mutations from a committed-action dispatch point, or guard them for idempotency.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(BaseCallFromInjected, SubscribeOutsideOnInit, MutatingVerbInUnsafeContext);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var attributeType = start.Compilation.GetTypeByMetadataName(JiangyuTypeAttributeMetadataName);
            var hookBus = start.Compilation.GetTypeByMetadataName(HookBusMetadataName);
            var mutatingVerb = start.Compilation.GetTypeByMetadataName(MutatingVerbAttributeMetadataName);

            if (attributeType is not null)
                start.RegisterSyntaxNodeAction(
                    node => AnalyzeBaseCall(node, attributeType),
                    SyntaxKind.InvocationExpression);

            if (hookBus is not null || mutatingVerb is not null)
                start.RegisterOperationAction(
                    operation => AnalyzeInvocation(operation, hookBus, mutatingVerb),
                    OperationKind.Invocation);
        });
    }

    // JIA007: base.Member(...) inside a method of a [JiangyuType] class.
    private static void AnalyzeBaseCall(SyntaxNodeAnalysisContext context, INamedTypeSymbol attributeType)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || member.Expression is not BaseExpressionSyntax)
            return;

        var typeDeclaration = invocation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeDeclaration is null)
            return;

        var typeSymbol = context.SemanticModel.GetDeclaredSymbol(typeDeclaration, context.CancellationToken);
        if (typeSymbol is null || !HasAttribute(typeSymbol, attributeType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            BaseCallFromInjected, member.Name.GetLocation(), member.Name.Identifier.Text));
    }

    // JIA008 and JIA009: dispatched on the call's target method.
    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol? hookBus, INamedTypeSymbol? mutatingVerb)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var containing = method.ContainingType?.OriginalDefinition;
        if (containing is null)
            return;

        if (hookBus is not null && method.Name == "Subscribe"
            && SymbolEqualityComparer.Default.Equals(containing, hookBus))
        {
            var enclosing = EnclosingMethodName(invocation.Syntax);
            var cadence = enclosing switch
            {
                "OnSceneLoaded" => "scene load",
                "OnUpdate" => "frame",
                _ => null,
            };
            if (cadence is not null)
                context.ReportDiagnostic(Diagnostic.Create(
                    SubscribeOutsideOnInit, invocation.Syntax.GetLocation(), enclosing, cadence));
            return;
        }

        if (mutatingVerb is not null && HasAttribute(method, mutatingVerb))
        {
            var enclosing = EnclosingMethodName(invocation.Syntax);
            var advice = UnsafeOverrideAdvice(enclosing);
            if (advice is not null)
                context.ReportDiagnostic(Diagnostic.Create(
                    MutatingVerbInUnsafeContext, invocation.Syntax.GetLocation(),
                    $"{containing.Name}.{method.Name}", enclosing, advice));
        }
    }

    // The advice for a mutating verb called from an override whose dispatch cadence
    // makes the mutation unsafe, or null when the enclosing method is safe.
    //
    // This set is curated dispatch-cadence knowledge the analyzer cannot derive from
    // the symbol graph: which override points are predicates (a mutation is refused at
    // runtime) or fire repeatedly (it compounds and re-fires on reload). The game-type
    // override names (IsTrue/OnUpdate/OnTurnStart on classes the SDK does not own)
    // cannot carry an attribute, and the JiangyuMod lifecycle names classify by
    // cadence, not identity. Extend it when a new predicate/repeated override ships.
    // (Which methods *mutate* is the part that is attribute-driven, via [MutatingVerb].)
    private static string? UnsafeOverrideAdvice(string? enclosingMethod) => enclosingMethod switch
    {
        "IsTrue" => "a predicate override runs during evaluation, where the verb is refused at runtime; trigger it from a committed-action dispatch point instead.",
        "OnUpdate" or "OnTurnStart" => "this override fires repeatedly, so the mutation compounds and re-fires on reload; guard it for idempotency or trigger it from a game event instead.",
        _ => null,
    };

    // The name of the method whose body DIRECTLY contains the node, or null when the
    // node is not directly in a method body. A lambda or local function is a boundary:
    // a call inside one runs at that delegate's cadence (often deferred — a coroutine
    // body, a hook handler), not the enclosing method's, so the cadence rules must not
    // attribute it to the enclosing override.
    private static string? EnclosingMethodName(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return null;
            if (current is MethodDeclarationSyntax method)
                return method.Identifier.Text;
            if (current is BaseTypeDeclarationSyntax)
                return null;
        }
        return null;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
        => symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
}
