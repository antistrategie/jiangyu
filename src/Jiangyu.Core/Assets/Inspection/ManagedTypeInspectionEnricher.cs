using System.Globalization;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

internal static class ManagedTypeInspectionEnricher
{
    public static void Enrich(IMonoBehaviour monoBehaviour, IAssemblyManager assemblyManager, List<InspectedFieldNode> fields)
    {
        ArgumentNullException.ThrowIfNull(monoBehaviour);
        ArgumentNullException.ThrowIfNull(assemblyManager);
        ArgumentNullException.ThrowIfNull(fields);

        IMonoScript? script = monoBehaviour.ScriptP;
        if (script is null)
        {
            return;
        }

        TypeDefinition typeDefinition;
        try
        {
            typeDefinition = script.GetTypeDefinition(assemblyManager);
        }
        catch
        {
            return;
        }

        InspectedFieldNode? structureNode = fields.FirstOrDefault(f => string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
        if (structureNode?.Fields is null)
        {
            return;
        }

        structureNode.FieldTypeName = script.GetFullName();
        EnrichFields(structureNode.Fields, typeDefinition);
    }

    private static void EnrichFields(List<InspectedFieldNode> fields, TypeDefinition typeDefinition)
    {
        Dictionary<string, ManagedFieldMetadata> metadataByName = BuildMetadataMap(typeDefinition);
        foreach (InspectedFieldNode field in fields)
        {
            if (field.Name is null || !metadataByName.TryGetValue(field.Name, out ManagedFieldMetadata? metadata))
            {
                continue;
            }

            field.FieldTypeName = metadata.DisplayName;
            if (metadata.IsEnum && metadata.ResolvedType is not null)
            {
                PromoteEnumScalar(field, metadata.ResolvedType);
            }

            if (field.Fields is { Count: > 0 } && metadata.ResolvedType is not null)
            {
                EnrichFields(field.Fields, metadata.ResolvedType);
            }
            else if (field.Elements is { Count: > 0 } && metadata.ElementResolvedType is not null)
            {
                bool elementIsEnum = metadata.IsElementEnum;
                // For [NamedArray(typeof(T))] byte/int arrays each slot pairs
                // with one enum member; surface its name on the element so
                // the inspect output reads as { "Vitality": 70 } rather than
                // { "[4]": 70 }. Computed once per field.
                IReadOnlyList<string>? namedArraySlots = metadata.NamedArrayEnum is not null
                    ? GetEnumMembersByValue(metadata.NamedArrayEnum)
                    : null;
                int idx = 0;
                foreach (InspectedFieldNode element in field.Elements)
                {
                    if (elementIsEnum)
                    {
                        PromoteEnumScalar(element, metadata.ElementResolvedType);
                    }
                    if (namedArraySlots is not null && idx < namedArraySlots.Count)
                    {
                        element.Name = namedArraySlots[idx];
                    }
                    if (element.Fields is { Count: > 0 })
                    {
                        EnrichFields(element.Fields, metadata.ElementResolvedType);
                    }
                    idx++;
                }
            }
        }
    }

    // An enum's reflected shape (the [Flags] marker plus its constants), read once per TypeDefinition.
    // The same enum recurs across thousands of template instances, so caching the attribute and field
    // walks here keeps a full re-index (and every Studio inspect) off a per-scalar metadata scan.
    private sealed class EnumInfo
    {
        public bool IsFlags;
        // Constants in declaration order: name lookup and flags decomposition both depend on order.
        public (long Value, string Name)[] Members = [];
        // Member names ordered by constant value, for the [NamedArray] element[i] ↔ value-i mapping.
        public string[] NamesByValue = [];
    }

    // Weak keys so entries die with their TypeDefinition when the game assembly is reloaded.
    private static readonly ConditionalWeakTable<TypeDefinition, EnumInfo> EnumCache = new();

    private static EnumInfo GetEnumInfo(TypeDefinition enumType) => EnumCache.GetValue(enumType, BuildEnumInfo);

    private static EnumInfo BuildEnumInfo(TypeDefinition enumType)
    {
        var members = new List<(long Value, string Name)>();
        foreach (FieldDefinition field in enumType.Fields)
        {
            if (!field.IsStatic || field.Constant is null) continue;
            if (!TryConvertConstantToLong(field.Constant, out long value)) continue;
            string? name = field.Name?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            members.Add((value, name));
        }
        bool isFlags = false;
        foreach (CustomAttribute attribute in enumType.CustomAttributes)
        {
            if (string.Equals(attribute.Constructor?.DeclaringType?.FullName, "System.FlagsAttribute", StringComparison.Ordinal))
            {
                isFlags = true;
                break;
            }
        }
        return new EnumInfo
        {
            IsFlags = isFlags,
            Members = [.. members],
            NamesByValue = [.. members.OrderBy(m => m.Value).Select(m => m.Name)],
        };
    }

    /// <summary>
    /// Returns an enum's member names ordered by their constant value, so callers can index by value
    /// (the <c>[NamedArray]</c> convention is element[i] ↔ enum member with value i).
    /// </summary>
    private static IReadOnlyList<string> GetEnumMembersByValue(TypeDefinition enumType) => GetEnumInfo(enumType).NamesByValue;

    // The field's declared type is an enum, so its kind is "enum" regardless of whether the value
    // names a single member. A single member shows by name; a [Flags] bitmask decomposes into the
    // " | "-joined names of its set members; any value that still can't be named keeps its number as
    // a string. Classifying every enum-typed scalar as "enum" (with a string Value) keeps the kind
    // stable across instances, so a flags combination on one template does not read as a different
    // kind than a single flag on another (which would otherwise break structural-baseline comparison).
    private static void PromoteEnumScalar(InspectedFieldNode node, TypeDefinition enumType)
    {
        if (!string.Equals(node.Kind, "int", StringComparison.Ordinal)) return;
        if (node.Value is null) return;
        if (!TryReadIntegerValue(node.Value, out long numericValue)) return;
        node.Kind = "enum";
        if (TryGetEnumMemberName(enumType, numericValue, out string? name))
            node.Value = name;
        else if (TryComposeFlags(enumType, numericValue, out string? composed))
            node.Value = composed;
        else
            node.Value = numericValue.ToString(CultureInfo.InvariantCulture);
    }

    // Decompose a [Flags] enum value into the " | "-joined names of the members it sets. Returns
    // false for a non-flags enum, the zero value, or when named members don't cover every set bit
    // (so the caller keeps the raw number rather than an incomplete name). Members are matched in
    // declaration order, each consuming the bits it contributes.
    private static bool TryComposeFlags(TypeDefinition enumType, long value, out string? composed)
    {
        composed = null;
        EnumInfo info = GetEnumInfo(enumType);
        if (value == 0 || !info.IsFlags) return false;
        long remaining = value;
        List<string> names = [];
        foreach ((long memberValue, string memberName) in info.Members)
        {
            if (memberValue == 0) continue;
            if ((value & memberValue) != memberValue || (remaining & memberValue) == 0) continue;
            names.Add(memberName);
            remaining &= ~memberValue;
        }
        if (names.Count == 0 || remaining != 0) return false;
        composed = string.Join(" | ", names);
        return true;
    }

    private static bool TryReadIntegerValue(object boxed, out long value)
    {
        switch (boxed)
        {
            case long l: value = l; return true;
            case int i: value = i; return true;
            case short s: value = s; return true;
            case sbyte sb: value = sb; return true;
            case byte b: value = b; return true;
            case ushort us: value = us; return true;
            case uint ui: value = ui; return true;
            case ulong ul: value = unchecked((long)ul); return true;
            default: value = 0; return false;
        }
    }

    private static bool TryGetEnumMemberName(TypeDefinition enumType, long value, out string? name)
    {
        foreach ((long memberValue, string memberName) in GetEnumInfo(enumType).Members)
        {
            if (memberValue == value)
            {
                name = memberName;
                return true;
            }
        }
        name = null;
        return false;
    }

    private static bool TryConvertConstantToLong(Constant constant, out long value)
    {
        value = 0;
        if (constant.Value is null) return false;
        try
        {
            object? boxed = constant.Value.InterpretData(constant.Type);
            if (boxed is null) return false;
            value = constant.Type == ElementType.U8
                ? unchecked((long)(ulong)boxed)
                : Convert.ToInt64(boxed);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static Dictionary<string, ManagedFieldMetadata> BuildMetadataMap(TypeDefinition typeDefinition)
    {
        var result = new Dictionary<string, ManagedFieldMetadata>(StringComparer.Ordinal);
        foreach (FieldDefinition field in EnumerateInstanceFields(typeDefinition))
        {
            string? fieldName = field.Name?.ToString();
            if (string.IsNullOrEmpty(fieldName))
            {
                continue;
            }

            var metadata = CreateMetadata(field.Signature?.FieldType);
            // Shape-based [NamedArray(typeof(T))] match: the game's attribute
            // is a typeof(enum) on a primitive-element array. Same heuristic
            // the catalog uses (TemplateTypeCatalog.GetNamedArrayEnumShortName)
            // so renames or il2cpp wrapping don't break detection.
            var namedArrayEnum = TryFindNamedArrayEnum(field, metadata);
            if (namedArrayEnum is not null)
            {
                metadata = metadata with { NamedArrayEnum = namedArrayEnum };
            }
            result[fieldName] = metadata;
        }
        return result;
    }

    private static TypeDefinition? TryFindNamedArrayEnum(FieldDefinition field, ManagedFieldMetadata metadata)
    {
        // Restrict to primitive-element arrays — same gate the catalog uses
        // to avoid matching unrelated attributes that happen to take
        // typeof(SomeEnum) as a constructor argument.
        if (metadata.ElementResolvedType is null || metadata.IsElementEnum) return null;
        if (field.CustomAttributes.Count == 0) return null;

        foreach (CustomAttribute attr in field.CustomAttributes)
        {
            if (attr.Signature is null) continue;
            foreach (var arg in attr.Signature.FixedArguments)
            {
                if (arg.Element is TypeSignature typeSig)
                {
                    var resolved = ResolveTypeDefinition(typeSig);
                    if (resolved is not null && IsEnumTypeDefinition(resolved)) return resolved;
                }
            }
        }
        return null;
    }

    private static bool IsEnumTypeDefinition(TypeDefinition typeDefinition)
    {
        TypeDefinition? current = typeDefinition;
        while (current is not null)
        {
            if (string.Equals(current.FullName, "System.Enum", StringComparison.Ordinal))
            {
                return true;
            }
            current = ResolveBaseType(current);
        }
        return false;
    }

    private static IEnumerable<FieldDefinition> EnumerateInstanceFields(TypeDefinition? typeDefinition)
    {
        if (typeDefinition is null)
        {
            yield break;
        }

        TypeDefinition? baseType = ResolveBaseType(typeDefinition);
        foreach (FieldDefinition field in EnumerateInstanceFields(baseType))
        {
            yield return field;
        }

        foreach (FieldDefinition field in typeDefinition.Fields)
        {
            if (!field.IsStatic)
            {
                yield return field;
            }
        }
    }

    private static ManagedFieldMetadata CreateMetadata(TypeSignature? typeSignature)
    {
        return typeSignature switch
        {
            null => new ManagedFieldMetadata("Unknown", false, null, null, false),
            SzArrayTypeSignature arrayType => new ManagedFieldMetadata(
                $"{GetDisplayName(arrayType.BaseType)}[]",
                false,
                null,
                ResolveTypeDefinition(arrayType.BaseType),
                IsEnumType(arrayType.BaseType)),
            GenericInstanceTypeSignature genericType => CreateGenericMetadata(genericType),
            _ => new ManagedFieldMetadata(
                GetDisplayName(typeSignature),
                IsEnumType(typeSignature),
                ResolveTypeDefinition(typeSignature),
                null,
                false),
        };
    }

    private static ManagedFieldMetadata CreateGenericMetadata(GenericInstanceTypeSignature genericType)
    {
        string displayName = GetDisplayName(genericType);
        TypeDefinition? resolvedType = ResolveTypeDefinition(genericType);
        TypeDefinition? elementType = null;
        bool elementIsEnum = false;
        if (genericType.TypeArguments.Count == 1)
        {
            TypeSignature elementSignature = genericType.TypeArguments[0];
            elementType = ResolveTypeDefinition(elementSignature);
            elementIsEnum = IsEnumType(elementSignature);
        }

        return new ManagedFieldMetadata(displayName, false, resolvedType, elementType, elementIsEnum);
    }

    private static string GetDisplayName(TypeSignature typeSignature)
    {
        return typeSignature switch
        {
            CorLibTypeSignature corlib => corlib.Name ?? corlib.ElementType.ToString(),
            TypeDefOrRefSignature typeDefOrRef => GetDisplayName(typeDefOrRef.Type),
            GenericInstanceTypeSignature generic => $"{GetDisplayName(generic.GenericType)}<{string.Join(", ", generic.TypeArguments.Select(GetDisplayName))}>",
            SzArrayTypeSignature array => $"{GetDisplayName(array.BaseType)}[]",
            ByReferenceTypeSignature byRef => $"{GetDisplayName(byRef.BaseType)}&",
            PointerTypeSignature pointer => $"{GetDisplayName(pointer.BaseType)}*",
            GenericParameterSignature parameter => parameter.Name ?? "T",
            _ => typeSignature.Name ?? typeSignature.ToString(),
        };
    }

    private static string GetDisplayName(ITypeDefOrRef type)
    {
        string? ns = type.Namespace?.ToString();
        string name = type.Name?.ToString() ?? "Unknown";

        if (string.IsNullOrEmpty(ns))
        {
            return name;
        }

        if (ns.StartsWith("System", StringComparison.Ordinal) ||
            ns.StartsWith("UnityEngine", StringComparison.Ordinal) ||
            ns.StartsWith("Menace", StringComparison.Ordinal) ||
            ns.StartsWith("Sirenix", StringComparison.Ordinal))
        {
            return $"{ns}.{name}";
        }

        return name;
    }

    private static bool IsEnumType(TypeSignature typeSignature)
    {
        TypeDefinition? typeDefinition = ResolveTypeDefinition(typeSignature);
        if (typeDefinition is null)
        {
            return false;
        }

        TypeDefinition? current = typeDefinition;
        while (current is not null)
        {
            if (string.Equals(current.FullName, "System.Enum", StringComparison.Ordinal))
            {
                return true;
            }
            current = ResolveBaseType(current);
        }

        return false;
    }

    private static TypeDefinition? ResolveTypeDefinition(TypeSignature? typeSignature)
    {
        try
        {
            return typeSignature switch
            {
                null => null,
                TypeDefOrRefSignature typeDefOrRef => typeDefOrRef.Type.Resolve(),
                GenericInstanceTypeSignature generic => generic.GenericType.Resolve(),
                SzArrayTypeSignature array => ResolveTypeDefinition(array.BaseType),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static TypeDefinition? ResolveBaseType(TypeDefinition typeDefinition)
    {
        try
        {
            return typeDefinition.BaseType?.Resolve();
        }
        catch
        {
            return null;
        }
    }

    private sealed record ManagedFieldMetadata(
        string DisplayName,
        bool IsEnum,
        TypeDefinition? ResolvedType,
        TypeDefinition? ElementResolvedType,
        bool IsElementEnum,
        TypeDefinition? NamedArrayEnum = null);
}
