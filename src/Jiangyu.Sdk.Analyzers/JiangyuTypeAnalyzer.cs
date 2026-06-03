using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Jiangyu.Sdk.Analyzers;

/// <summary>
/// Surfaces, at author time in the IDE, the <c>[JiangyuType]</c> mistakes the
/// loader rejects at scan time: a non-concrete class, an invalid bare name, and
/// an <c>ns:Name</c> collision. Each rule mirrors the loader's own gate, so a
/// flagged type is one that would silently fail to load.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JiangyuTypeAnalyzer : DiagnosticAnalyzer
{
    public const string JiangyuTypeAttributeMetadataName = "Jiangyu.Sdk.JiangyuTypeAttribute";

    private static readonly DiagnosticDescriptor MustBeConcreteClass = new(
        id: "JIA001",
        title: "[JiangyuType] must be a concrete class",
        messageFormat: "[JiangyuType] '{0}' must be a concrete class; an abstract or static class cannot be injected and the loader drops it",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Jiangyu injects [JiangyuType] classes into the IL2CPP type system at load. An abstract or static class cannot be instantiated, so the loader drops it and the type never reaches a KDL type= slot.");

    private static readonly DiagnosticDescriptor InvalidName = new(
        id: "JIA002",
        title: "[JiangyuType] name is invalid",
        messageFormat: "[JiangyuType] name '{0}' is invalid: it must be non-empty and must not contain ':'",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The bare name is namespaced as 'ns:Name' using the mod id. A ':' in the name breaks that resolution, and an empty name resolves to nothing. Omit the argument to use the class name.");

    private static readonly DiagnosticDescriptor NameCollision = new(
        id: "JIA003",
        title: "[JiangyuType] name collision",
        messageFormat: "[JiangyuType] name '{0}' is declared by more than one type; each resolves to the same 'ns:{0}' and collides at load time",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Two types resolving to the same ns:Name are ambiguous. The loader keeps one and drops the rest, so KDL type= references may bind to the wrong type.");

    private static readonly DiagnosticDescriptor FieldNotSerialisable = new(
        id: "JIA004",
        title: "[JiangyuType] field cannot be serialised",
        messageFormat: "[JiangyuType] field '{0}' of type '{1}' cannot be serialised; its value is lost when the game saves the injected instance",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The game constructs and serialises an injected [JiangyuType]. A delegate, pointer, or native-int field has no serialisable representation, so its value does not survive a save and load.");

    private static readonly DiagnosticDescriptor MissingInjectionConstructors = new(
        id: "JIA005",
        title: "[JiangyuType] needs the IL2CPP injection constructors",
        messageFormat: "[JiangyuType] '{0}' is missing the IL2CPP injection constructors. Mark it 'partial' so Jiangyu generates them, or declare them yourself.",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An injected [JiangyuType] needs an IntPtr constructor (to wrap an existing native object) and a parameterless constructor (to allocate a new one). Jiangyu's source generator emits both onto a partial class. A non-partial class that declares neither fails to inject at load.");

    private static readonly DiagnosticDescriptor CannotBeInjected = new(
        id: "JIA006",
        title: "[JiangyuType] cannot receive the IL2CPP injection constructors",
        messageFormat: "[JiangyuType] '{0}' cannot be injected: {1}",
        category: "Jiangyu",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Some shapes cannot take the IL2CPP injection constructors at all, so neither the source generator nor a hand-written constructor can make them inject. A record, a type nested in a generic type, a primary constructor, and a private injection constructor each block injection and need a different declaration.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MustBeConcreteClass, InvalidName, NameCollision, FieldNotSerialisable, MissingInjectionConstructors, CannotBeInjected);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var attributeType = start.Compilation.GetTypeByMetadataName(JiangyuTypeAttributeMetadataName);
            if (attributeType is null)
                return;

            // How many types resolve to each bare name across the assembly, built
            // once so the per-symbol collision check (live in the IDE) is a lookup.
            var countByName = BuildNameCounts(start.Compilation, attributeType, start.CancellationToken);

            start.RegisterSymbolAction(
                symbolContext => Inspect((INamedTypeSymbol)symbolContext.Symbol, attributeType, countByName, symbolContext),
                SymbolKind.NamedType);
        });
    }

    private static Dictionary<string, int> BuildNameCounts(
        Compilation compilation, INamedTypeSymbol attributeType, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var symbol in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, cancellationToken))
        {
            if (symbol is not INamedTypeSymbol type)
                continue;
            if (EffectiveName(type, attributeType) is { } name)
                counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static void Inspect(
        INamedTypeSymbol type,
        INamedTypeSymbol attributeType,
        Dictionary<string, int> countByName,
        SymbolAnalysisContext context)
    {
        var attribute = FindAttribute(type, attributeType);
        if (attribute is null)
            return;

        var location = AttributeLocation(attribute, context.CancellationToken)
            ?? type.Locations.FirstOrDefault()
            ?? Location.None;

        if (type.IsAbstract || type.IsStatic)
        {
            context.ReportDiagnostic(Diagnostic.Create(MustBeConcreteClass, location, type.Name));
            return;
        }

        var explicitName = ExplicitName(attribute);
        if (explicitName is not null && (string.IsNullOrWhiteSpace(explicitName) || explicitName.Contains(':')))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidName, location, explicitName));
            return;
        }

        var bareName = explicitName ?? type.Name;
        if (countByName.TryGetValue(bareName, out var count) && count > 1)
            context.ReportDiagnostic(Diagnostic.Create(NameCollision, location, bareName));

        // Injection-constructor diagnostics apply only to a class whose base can take the
        // generated base(IntPtr) call (object/managed-rooted types are not injected at
        // all). Within that: a structural blocker the generator can never emit for is a
        // JIA006 error; otherwise a non-partial class missing the constructors gets the
        // JIA005 'mark partial' warning (the generator supplies them for a partial class).
        if (type.TypeKind == TypeKind.Class && !type.IsGenericType
            && JiangyuTypeFacts.BaseAcceptsInjection(type))
        {
            if (JiangyuTypeFacts.UninjectableReason(type) is { } reason)
                context.ReportDiagnostic(Diagnostic.Create(CannotBeInjected, location, type.Name, reason));
            else if (MissingInjectionConstructor(type) && !IsPartial(type))
                context.ReportDiagnostic(Diagnostic.Create(MissingInjectionConstructors, location, type.Name));
        }

        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                continue;
            if (IsUnserialisable(field.Type))
                context.ReportDiagnostic(Diagnostic.Create(
                    FieldNotSerialisable,
                    field.Locations.FirstOrDefault() ?? location,
                    field.Name,
                    field.Type.ToDisplayString()));
        }
    }

    // A non-partial class cannot have the injection constructors generated for it, so
    // it must declare them itself. Partial classes are left to the generator.
    private static bool IsPartial(INamedTypeSymbol type)
        => type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)));

    // True when the type lacks either the IntPtr or the parameterless constructor that
    // IL2CPP injection requires.
    private static bool MissingInjectionConstructor(INamedTypeSymbol type)
    {
        var (hasPointer, hasParameterless) = JiangyuTypeFacts.InjectionConstructors(type);
        return !(hasPointer && hasParameterless);
    }

    // Field types with no serialisable representation on any runtime, so the game's
    // save cannot persist them on an injected instance. Deliberately narrow: types
    // whose round-trip on an injected IL2CPP type is uncertain are left unflagged.
    private static bool IsUnserialisable(ITypeSymbol type)
        => type.TypeKind is TypeKind.Delegate or TypeKind.Pointer or TypeKind.FunctionPointer
            || type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr;

    // The bare name a [JiangyuType] resolves to, or null when the type does not
    // participate in a collision: no attribute, or one the loader drops before its
    // collision check. The loader's gate is "concrete, non-generic class", so a
    // non-class (struct/enum/interface) or a generic type (which, in Roslyn, includes a
    // type nested in a generic type) never reaches the collision check and must not be
    // counted here, or a dropped type would raise a phantom JIA003 against a survivor.
    private static string? EffectiveName(INamedTypeSymbol type, INamedTypeSymbol attributeType)
    {
        var attribute = FindAttribute(type, attributeType);
        if (attribute is null || type.IsAbstract || type.IsStatic
            || type.TypeKind != TypeKind.Class || type.IsGenericType)
            return null;

        var explicitName = ExplicitName(attribute);
        if (explicitName is not null && (string.IsNullOrWhiteSpace(explicitName) || explicitName.Contains(':')))
            return null;

        return explicitName ?? type.Name;
    }

    private static AttributeData? FindAttribute(INamedTypeSymbol type, INamedTypeSymbol attributeType)
        => type.GetAttributes().FirstOrDefault(
            a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

    private static Location? AttributeLocation(AttributeData attribute, CancellationToken cancellationToken)
        => attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();

    // The bare name argument when one is supplied, else null (the loader falls back
    // to the class name, which is always a valid bare name).
    private static string? ExplicitName(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
            return null;
        var argument = attribute.ConstructorArguments[0];
        return argument.IsNull ? null : argument.Value as string;
    }
}
