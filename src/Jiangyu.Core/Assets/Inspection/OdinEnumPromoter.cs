using AsmResolver.DotNet;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates.Odin;
using static Jiangyu.Core.Assets.ManagedTypeInspectionEnricher;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Names the enum values inside a decoded Odin payload. Odin writes an enum
/// as its integer value, and the decoder recovers only the class names the
/// blob carries, so every enum scalar and enum-array element decodes as
/// <c>Kind == "int"</c>. This pass resolves each decoded object's class
/// against the game assembly, reads its field metadata the same way
/// <see cref="ManagedTypeInspectionEnricher"/> does for Unity-native fields,
/// and promotes the matching nodes to <c>Kind == "enum"</c> with the member
/// name as the value.
/// </summary>
/// <remarks>
/// A decoded object's own class name takes precedence over the type its
/// field declares: a handler field declared <c>ITacticalCondition</c> holds a
/// concrete condition, and only the concrete class knows which of its fields
/// are enums. Nodes whose class the resolver cannot find keep their integer
/// values, and so do the cells of a multi-dimensional array (<c>Kind ==
/// "matrix"</c>): the Studio matrix editor keys its flags grid off integer
/// cells, so those stay numeric by design.
/// </remarks>
internal static class OdinEnumPromoter
{
    /// <param name="nodes">The decoded top-level fields of one payload.</param>
    /// <param name="ownerTypeName">Full name of the class that owns those
    /// top-level fields (the script class of the inspected object), or
    /// <c>null</c> when unknown.</param>
    /// <param name="resolve">Full-name lookup into the game assembly.</param>
    public static void Promote(
        List<InspectedFieldNode> nodes,
        string? ownerTypeName,
        Func<string, TypeDefinition?> resolve)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(resolve);
        PromoteFields(nodes, ResolveByName(ownerTypeName, resolve), resolve);
    }

    private static void PromoteFields(
        List<InspectedFieldNode> fields,
        TypeDefinition? ownerType,
        Func<string, TypeDefinition?> resolve)
    {
        var metadata = ownerType is null ? null : TryGetMetadataMap(ownerType);
        foreach (var field in fields)
        {
            ManagedFieldMetadata? declared = null;
            if (metadata is not null && field.Name is not null)
                metadata.TryGetValue(field.Name, out declared);
            PromoteNode(field, declared, resolve);
        }
    }

    private static void PromoteNode(
        InspectedFieldNode node,
        ManagedFieldMetadata? declared,
        Func<string, TypeDefinition?> resolve)
    {
        // "matrix" is deliberately absent: see the class remarks.
        switch (node.Kind)
        {
            case "int":
                if (declared is { IsEnum: true, ResolvedType: { } enumType })
                    PromoteScalar(node, enumType, declared.DisplayName);
                break;

            case "object":
                if (node.Fields is { Count: > 0 })
                {
                    var concrete = ResolveByName(node.FieldTypeName, resolve) ?? declared?.ResolvedType;
                    PromoteFields(node.Fields, concrete, resolve);
                }
                break;

            case "array":
                if (node.Elements is { Count: > 0 })
                {
                    var elementType = ResolveByName(TryGetElementTypeName(node.FieldTypeName), resolve)
                        ?? declared?.ElementResolvedType;
                    PromoteElements(node.Elements, elementType, resolve);
                }
                break;
        }
    }

    private static void PromoteElements(
        List<InspectedFieldNode> elements,
        TypeDefinition? elementType,
        Func<string, TypeDefinition?> resolve)
    {
        if (elementType is not null && IsEnumTypeDefinition(elementType))
        {
            var displayName = GetDisplayName(elementType);
            foreach (var element in elements)
                PromoteScalar(element, elementType, displayName);
            return;
        }

        foreach (var element in elements)
        {
            if (element.Kind == "object")
            {
                if (element.Fields is { Count: > 0 })
                    PromoteFields(element.Fields, ResolveByName(element.FieldTypeName, resolve) ?? elementType, resolve);
            }
            else
            {
                PromoteNode(element, declared: null, resolve);
            }
        }
    }

    private static void PromoteScalar(InspectedFieldNode node, TypeDefinition enumType, string displayName)
    {
        PromoteEnumScalar(node, enumType);
        if (string.Equals(node.Kind, "enum", StringComparison.Ordinal))
            node.FieldTypeName ??= displayName;
    }

    // Reading a class's field metadata resolves signatures and parses
    // attribute blobs; an unreadable class leaves its own enums as integers
    // rather than failing the whole inspection.
    private static IReadOnlyDictionary<string, ManagedFieldMetadata>? TryGetMetadataMap(TypeDefinition type)
    {
        try
        {
            return GetMetadataMap(type);
        }
        catch
        {
            return null;
        }
    }

    private static TypeDefinition? ResolveByName(string? typeName, Func<string, TypeDefinition?> resolve)
        => string.IsNullOrEmpty(typeName) ? null : resolve(typeName);

    /// <summary>
    /// Element type name of a collection type name as the Odin binder
    /// displays it: <c>Menace.Tags.TagType[]</c> gives
    /// <c>Menace.Tags.TagType</c>, and a single-argument generic such as
    /// <c>System.Collections.Generic.List`1[[Menace.Tags.TagType, Assembly-CSharp]]</c>
    /// gives the argument minus its assembly suffix. Returns <c>null</c> for
    /// anything else, including multi-argument generics and multi-dimensional
    /// arrays. This reads the assembly-qualified notation the Odin blob
    /// carries, not the <c>List&lt;X&gt;</c> display form
    /// <see cref="ManagedTypeInspectionEnricher"/> writes on Unity-native
    /// fields.
    /// </summary>
    internal static string? TryGetElementTypeName(string? collectionTypeName)
    {
        if (string.IsNullOrEmpty(collectionTypeName))
            return null;

        if (collectionTypeName.EndsWith("[]", StringComparison.Ordinal))
            return collectionTypeName[..^2];

        var open = collectionTypeName.IndexOf("[[", StringComparison.Ordinal);
        var close = collectionTypeName.LastIndexOf("]]", StringComparison.Ordinal);
        if (open < 0 || close <= open)
            return null;

        var inner = collectionTypeName[(open + 2)..close];
        if (inner.Contains("],[", StringComparison.Ordinal))
            return null;

        return OdinTypeNameBinder.StripAssemblySuffix(inner);
    }
}
