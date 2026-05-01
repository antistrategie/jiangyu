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
                foreach (InspectedFieldNode element in field.Elements)
                {
                    if (elementIsEnum)
                    {
                        PromoteEnumScalar(element, metadata.ElementResolvedType);
                    }
                    if (element.Fields is { Count: > 0 })
                    {
                        EnrichFields(element.Fields, metadata.ElementResolvedType);
                    }
                }
            }
        }
    }

    // Skips when the integer value doesn't map to a single enum member: Flags
    // bitmasks combine multiple members, and unknown raw values can't be named
    // safely. Leaving such nodes as ints lets the frontend fall back to a
    // neutral default rather than emit a synthesised member name.
    private static void PromoteEnumScalar(InspectedFieldNode node, TypeDefinition enumType)
    {
        if (!string.Equals(node.Kind, "int", StringComparison.Ordinal)) return;
        if (node.Value is null) return;
        if (!TryReadIntegerValue(node.Value, out long numericValue)) return;
        if (!TryGetEnumMemberName(enumType, numericValue, out string? name)) return;
        node.Kind = "enum";
        node.Value = name;
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
        name = null;
        foreach (FieldDefinition field in enumType.Fields)
        {
            if (!field.IsStatic || field.Constant is null) continue;
            if (!TryConvertConstantToLong(field.Constant, out long memberValue)) continue;
            if (memberValue != value) continue;
            string? candidate = field.Name?.ToString();
            if (string.IsNullOrEmpty(candidate)) return false;
            name = candidate;
            return true;
        }
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

            result[fieldName] = CreateMetadata(field.Signature?.FieldType);
        }
        return result;
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
        bool IsElementEnum);
}
