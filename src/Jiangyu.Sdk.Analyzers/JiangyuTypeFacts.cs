using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Jiangyu.Sdk.Analyzers;

/// <summary>
/// The shape facts about a <c>[JiangyuType]</c> that the generator (which constructors
/// to emit) and the analyzer (which to warn about) must agree on, kept in one place so
/// the two never drift.
/// </summary>
internal static class JiangyuTypeFacts
{
    /// <summary>
    /// Whether the type already declares each IL2CPP injection constructor: an
    /// <c>IntPtr</c> one to wrap an existing native object, and a parameterless one to
    /// allocate a fresh one. The implicit default constructor does not count.
    /// </summary>
    public static (bool HasPointer, bool HasParameterless) InjectionConstructors(INamedTypeSymbol type)
    {
        var hasPointer = false;
        var hasParameterless = false;
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.IsImplicitlyDeclared)
                continue;
            if (ctor.Parameters.Length == 0)
                hasParameterless = true;
            else if (ctor.Parameters.Length == 1 && ctor.Parameters[0].Type.SpecialType == SpecialType.System_IntPtr)
                hasPointer = true;
        }
        return (hasPointer, hasParameterless);
    }

    /// <summary>
    /// Whether the type's base accepts the <c>base(IntPtr)</c> call the generated
    /// constructors forward to, i.e. the base declares an accessible single-IntPtr
    /// constructor. Every IL2CPP type has one; object and plain managed bases that lack
    /// one do not, and the generated constructors would not compile against them. This
    /// is the structural signal for "injectable", so neither the generator nor the
    /// missing-constructor warning applies to a type whose base cannot take it.
    /// </summary>
    public static bool BaseAcceptsInjection(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType is null)
            return false;

        foreach (var ctor in baseType.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                continue;
            if (ctor.Parameters.Length == 1 && ctor.Parameters[0].Type.SpecialType == SpecialType.System_IntPtr)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Why a type that derives an injectable base still cannot receive the injection
    /// constructors, or null when it can. The generator skips emitting for these (the
    /// emitted code would not compile or would inject an unusable type) and the analyzer
    /// reports the same reason, so the two never disagree on what is injectable.
    /// </summary>
    public static string? UninjectableReason(INamedTypeSymbol type)
    {
        if (type.IsRecord)
            return "a record cannot receive the injection constructors; declare it as a class";
        if (HasPrimaryConstructor(type))
            return "a primary constructor prevents the injection constructors; remove it";
        if (HasPrivateInjectionConstructor(type))
            return "an injection constructor is private; the injector cannot call it, so make it public";
        return null;
    }

    // A class with a primary constructor requires every other constructor to chain to it
    // via this(...), which the generated base(...) constructors cannot, so they would not
    // compile.
    private static bool HasPrimaryConstructor(INamedTypeSymbol type)
        => type.DeclaringSyntaxReferences.Any(
            reference => reference.GetSyntax() is TypeDeclarationSyntax { ParameterList: not null });

    // A strictly-private parameterless or single-IntPtr constructor cannot be replaced by
    // the generator (a second one would collide) and the injector cannot call it.
    private static bool HasPrivateInjectionConstructor(INamedTypeSymbol type)
    {
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.IsImplicitlyDeclared || ctor.DeclaredAccessibility != Accessibility.Private)
                continue;
            if (ctor.Parameters.Length == 0)
                return true;
            if (ctor.Parameters.Length == 1 && ctor.Parameters[0].Type.SpecialType == SpecialType.System_IntPtr)
                return true;
        }
        return false;
    }
}
