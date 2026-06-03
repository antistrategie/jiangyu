using System.Reflection;

namespace Jiangyu.Core.Code;

/// <summary>A <c>[JiangyuType]</c> found in a mod's built code DLL.</summary>
public sealed record JiangyuTypeInfo(
    string Name,
    string FullName,
    string? BaseType,
    IReadOnlyList<JiangyuFieldInfo> Fields);

public sealed record JiangyuFieldInfo(string Name, string Type);

/// <summary>
/// Reflects a mod's built code DLLs (metadata-only, no execution) to list the
/// <c>[JiangyuType]</c> types it defines with their base type and serialisable
/// fields. Reuses the same reference-resolution shape as the compile cross-check
/// so a field whose type lives in a game or SDK assembly still resolves to a name.
/// </summary>
public static class CodeTypeInspector
{
    public static IReadOnlyList<JiangyuTypeInfo> Inspect(IReadOnlyList<string> dllPaths, string? gameDir, string? sdkDir)
    {
        var result = new List<JiangyuTypeInfo>();
        JiangyuTypeMetadataReader.Read(dllPaths, gameDir, sdkDir,
            visit: (type, _, bareName) => result.Add(new JiangyuTypeInfo(
                bareName,
                type.FullName ?? type.Name,
                SafeBaseTypeName(type),
                ReadFields(type))));
        return result;
    }

    private static IReadOnlyList<JiangyuFieldInfo> ReadFields(Type type)
    {
        var fields = new List<JiangyuFieldInfo>();
        FieldInfo[] declared;
        try { declared = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
        catch { return fields; }

        foreach (var field in declared)
        {
            if (field.IsStatic || field.IsLiteral)
                continue;
            string typeName;
            try { typeName = field.FieldType.FullName ?? field.FieldType.Name; }
            catch { typeName = "?"; }
            fields.Add(new JiangyuFieldInfo(field.Name, typeName));
        }
        return fields;
    }

    private static string? SafeBaseTypeName(Type type)
    {
        try { return type.BaseType?.FullName; }
        catch { return null; }
    }
}
