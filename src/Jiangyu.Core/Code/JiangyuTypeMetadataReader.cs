using System.Reflection;
using Jiangyu.Core.Reflection;

namespace Jiangyu.Core.Code;

/// <summary>
/// Opens a metadata-only reflection context over a mod's built code DLLs, with the
/// game and SDK assemblies on the resolver path, and visits each <c>[JiangyuType]</c>
/// it finds with the type, the attribute data, and the resolved bare name. Both the
/// compile-time cross-check and the MCP type inspector read <c>[JiangyuType]</c>s
/// through here, so the context setup and name resolution live in one place.
/// </summary>
internal static class JiangyuTypeMetadataReader
{
    public static void Read(
        IReadOnlyList<string> dllPaths,
        string? gameDir,
        string? sdkDir,
        Action<Type, CustomAttributeData, string> visit,
        Action<string>? onError = null)
    {
        if (dllPaths.Count == 0)
            return;

        var searchDirs = new[]
        {
            Path.GetDirectoryName(dllPaths[0]),
            sdkDir,
            gameDir is null ? null : Path.Combine(gameDir, "MelonLoader", "Il2CppAssemblies"),
            gameDir is null ? null : Path.Combine(gameDir, "MelonLoader", "net6"),
            Path.GetDirectoryName(typeof(object).Assembly.Location),
        };

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                if (seen.Add(Path.GetFileName(dll)))
                    paths.Add(dll);
        }

        try
        {
            var resolver = new PathAssemblyResolver(paths);
            using var context = new MetadataLoadContext(resolver);
            foreach (var dllPath in dllPaths)
            {
                Assembly assembly;
                // Load the mod's own DLL from a byte copy, never the file path. On Windows
                // MetadataLoadContext memory-maps a path-loaded assembly and does not reliably
                // release the handle on dispose, so the inspected DLL stays locked. Since the
                // inspected DLLs are the ones a later compile deletes or overwrites (compiled/
                // gets wiped, code/bin rebuilt), a lingering lock from a long-lived host (Studio,
                // the MCP server) makes that compile fail with "used by another process". Reading
                // the bytes first leaves no handle on the file. Dependency assemblies resolve via
                // the path resolver, which is fine: those are never deleted.
                try { assembly = context.LoadFromByteArray(File.ReadAllBytes(dllPath)); }
                catch { continue; }

                foreach (var type in SafeGetTypes(assembly))
                {
                    var attr = FindJiangyuTypeAttribute(type);
                    if (attr is null) continue;

                    visit(type, attr, BareName(type, attr));
                }
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>ns:Name</c> bare name a <c>[JiangyuType]</c> resolves to (the attribute's
    /// name argument, else the CLR type name), or null when the type carries no
    /// <c>[JiangyuType]</c>. Reads attribute metadata only, so it is safe on a
    /// <see cref="MetadataLoadContext"/>-loaded type from any catalog.
    /// </summary>
    public static string? BareName(Type type)
    {
        var attr = FindJiangyuTypeAttribute(type);
        return attr is null ? null : BareName(type, attr);
    }

    // The [JiangyuType] attribute on a type, or null. Reading AttributeType resolves the
    // attribute's assembly, so an attribute the resolver can't load is skipped rather
    // than allowed to crash (or abort) the whole [JiangyuType] scan.
    private static CustomAttributeData? FindJiangyuTypeAttribute(Type type)
    {
        foreach (var attr in type.GetCustomAttributesData())
        {
            string attributeName;
            try { attributeName = attr.AttributeType.Name; }
            catch { continue; }
            if (attributeName == "JiangyuTypeAttribute")
                return attr;
        }
        return null;
    }

    private static string BareName(Type type, CustomAttributeData attribute)
        => attribute.ConstructorArguments.Count > 0
            && attribute.ConstructorArguments[0].Value is string named
            && !string.IsNullOrWhiteSpace(named)
            ? named
            : type.Name;

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly) => AssemblyTypes.SafeGetTypes(assembly);
}
