using System;
using System.Collections.Generic;
using System.Reflection;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk;

/// <summary>A discovered <c>[JiangyuType]</c> in a mod assembly, with its ns:Name resolved.</summary>
internal sealed class JiangyuTypeEntry
{
    public JiangyuTypeEntry(string qualifiedName, Type managedType, IReadOnlyList<Type> interfaces)
    {
        QualifiedName = qualifiedName;
        ManagedType = managedType;
        Interfaces = interfaces;
    }

    public string QualifiedName { get; }
    public Type ManagedType { get; }

    /// <summary>The game IL2CPP interfaces the type satisfies (from the attribute),
    /// or empty when it injects by deriving a game base class.</summary>
    public IReadOnlyList<Type> Interfaces { get; }
}

/// <summary>The outcome of scanning a mod assembly: the valid entries and any errors.</summary>
internal sealed class JiangyuTypeScan
{
    public JiangyuTypeScan(IReadOnlyList<JiangyuTypeEntry> entries, IReadOnlyList<string> errors)
    {
        Entries = entries;
        Errors = errors;
    }

    public IReadOnlyList<JiangyuTypeEntry> Entries { get; }
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Pure-reflection discovery of <c>[JiangyuType]</c> types in a mod assembly. It
/// resolves each to its <c>ns:Name</c> (ns is the mod id) and detects collisions.
/// </summary>
internal static class JiangyuTypeCatalog
{
    public static JiangyuTypeScan Scan(Assembly modAssembly, string modId)
    {
        var entries = new List<JiangyuTypeEntry>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(modId) || modId.Contains(':'))
        {
            errors.Add($"invalid mod id '{modId}': must be non-empty and contain no ':'.");
            return new JiangyuTypeScan(entries, errors);
        }

        var seen = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var type in ModAssemblyScan.SafeGetTypes(modAssembly))
        {
            var attr = type.GetCustomAttribute<JiangyuTypeAttribute>();
            if (attr == null)
                continue;

            if (!type.IsClass || type.IsAbstract || type.IsGenericType)
            {
                errors.Add($"[JiangyuType] {type.FullName}: must be a concrete, non-generic class.");
                continue;
            }

            var bareName = attr.Name ?? type.Name;
            if (string.IsNullOrWhiteSpace(bareName) || bareName.Contains(':'))
            {
                errors.Add($"[JiangyuType] {type.FullName}: invalid name '{bareName}' (non-empty, no ':').");
                continue;
            }

            var qualified = $"{modId}:{bareName}";
            if (seen.TryGetValue(qualified, out var prior))
            {
                errors.Add($"[JiangyuType] name collision '{qualified}': {prior.FullName} and {type.FullName}.");
                continue;
            }

            seen[qualified] = type;
            entries.Add(new JiangyuTypeEntry(qualified, type, attr.Interfaces ?? Array.Empty<Type>()));
        }

        return new JiangyuTypeScan(entries, errors);
    }
}
