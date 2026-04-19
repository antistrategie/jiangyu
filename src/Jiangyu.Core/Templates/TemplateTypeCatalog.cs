using System.Reflection;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Offline type catalogue for the Il2CppInterop-generated Assembly-CSharp.dll.
/// Opens the assembly via <see cref="MetadataLoadContext"/> so callers can
/// enumerate live-compatible wrapper types, their writable members, and the
/// element type of collection members — without hosting MelonLoader or loading
/// the game into memory.
///
/// Consumers are the <c>jiangyu templates query</c> CLI and tests that need to
/// assert the modder-facing shape of a template type. The catalogue walks the
/// hierarchy up to (but not including) <c>Il2CppObjectBase</c> so interop-base
/// members don't leak into the modder-facing view, matching the behaviour of
/// <c>RuntimeInspector.BuildRuntimeTypeShape</c>.
/// </summary>
public sealed class TemplateTypeCatalog : IDisposable
{
    private const string Il2CppObjectBaseFullName = "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase";
    private const string Il2CppSystemListFullName = "Il2CppSystem.Collections.Generic.List`1";
    private const string BclListFullName = "System.Collections.Generic.List`1";

    // Il2CppInterop-generated wrappers for native Il2Cpp arrays. Both expose
    // a writable int indexer on the element type, so callers (query nav + the
    // runtime applier) can treat them the same as a managed T[].
    private static readonly HashSet<string> Il2CppArrayDefinitionFullNames = new(StringComparer.Ordinal)
    {
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1",
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1",
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1",
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray",
    };

    private readonly MetadataLoadContext _context;
    private readonly Assembly _assembly;
    private readonly Type[] _allTypes;

    private TemplateTypeCatalog(MetadataLoadContext context, Assembly assembly, Type[] allTypes)
    {
        _context = context;
        _assembly = assembly;
        _allTypes = allTypes;
    }

    public IReadOnlyList<Type> AllTypes => _allTypes;

    public static TemplateTypeCatalog Load(string assemblyPath, IEnumerable<string>? additionalSearchDirectories = null)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

        var searchDirectories = new List<string> { Path.GetDirectoryName(assemblyPath)! };
        if (additionalSearchDirectories != null)
            searchDirectories.AddRange(additionalSearchDirectories);

        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        searchDirectories.Add(runtimeDirectory);

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in searchDirectories)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                continue;
            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
            {
                if (seen.Add(dll))
                    paths.Add(dll);
            }
        }

        var resolver = new PathAssemblyResolver(paths);
        var context = new MetadataLoadContext(resolver);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t != null).ToArray()!;
        }

        return new TemplateTypeCatalog(context, assembly, allTypes);
    }

    /// <summary>
    /// Resolve <paramref name="nameOrFullName"/> to a non-abstract type in the
    /// assembly. Accepts both short names (<c>EntityTemplate</c>) and
    /// fully-qualified names (<c>Il2CppMenace.Tactical.EntityTemplate</c>).
    /// Populates <paramref name="ambiguousCandidates"/> when a short name
    /// matches multiple types.
    /// </summary>
    public Type? ResolveType(
        string nameOrFullName,
        out IReadOnlyList<Type> ambiguousCandidates,
        out string? error)
    {
        ambiguousCandidates = [];

        if (string.IsNullOrWhiteSpace(nameOrFullName))
        {
            error = "type name is empty.";
            return null;
        }

        var exact = _allTypes.FirstOrDefault(t => t.FullName == nameOrFullName);
        if (exact != null)
        {
            error = null;
            return exact;
        }

        var shortMatches = _allTypes
            .Where(t => t.Name == nameOrFullName)
            .ToArray();

        if (shortMatches.Length == 1)
        {
            error = null;
            return shortMatches[0];
        }

        if (shortMatches.Length > 1)
        {
            ambiguousCandidates = shortMatches;
            error = $"type name '{nameOrFullName}' is ambiguous.";
            return null;
        }

        error = $"no type '{nameOrFullName}' found in the assembly.";
        return null;
    }

    /// <summary>
    /// Returns the writable members of <paramref name="type"/> and its base
    /// types up to (but not including) <c>Il2CppObjectBase</c>. When
    /// <paramref name="includeReadOnly"/> is true, read-only members are
    /// included with <see cref="MemberShape.IsWritable"/>=false.
    /// </summary>
    public static IReadOnlyList<MemberShape> GetMembers(Type type, bool includeReadOnly = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<MemberShape>();

        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        for (var current = type; current != null && !IsStopBase(current); current = current.BaseType)
        {
            foreach (var property in current.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;

                if (!seen.Add("P:" + property.Name))
                    continue;

                var writable = property.CanWrite;
                if (!writable && !includeReadOnly)
                    continue;

                members.Add(new MemberShape(
                    Name: property.Name,
                    Kind: MemberKind.Property,
                    MemberType: property.PropertyType,
                    DeclaringTypeFullName: current.FullName ?? current.Name,
                    IsInherited: !ReferenceEquals(current, type),
                    IsWritable: writable));
            }

            foreach (var field in current.GetFields(flags))
            {
                if (!seen.Add("F:" + field.Name))
                    continue;

                var writable = !field.IsInitOnly && !field.IsLiteral;
                if (!writable && !includeReadOnly)
                    continue;

                members.Add(new MemberShape(
                    Name: field.Name,
                    Kind: MemberKind.Field,
                    MemberType: field.FieldType,
                    DeclaringTypeFullName: current.FullName ?? current.Name,
                    IsInherited: !ReferenceEquals(current, type),
                    IsWritable: writable));
            }
        }

        members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return members;
    }

    /// <summary>
    /// Returns the element type when <paramref name="type"/> is an array or an
    /// Il2Cpp <c>List&lt;T&gt;</c>, otherwise null. The CLI auto-unwraps these
    /// during navigation so <c>Skills</c> and <c>Skills[0]</c> both resolve to
    /// the underlying element type.
    /// </summary>
    public static Type? GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        // Walk the base chain so Il2CppStructArray<T> / Il2CppReferenceArray<T>
        // (and any future subclass of Il2CppArrayBase<T>) unwrap to T without
        // having to enumerate every leaf wrapper here.
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!current.IsGenericType)
                continue;

            var definitionName = current.GetGenericTypeDefinition().FullName;
            if (definitionName == Il2CppSystemListFullName
                || definitionName == BclListFullName
                || Il2CppArrayDefinitionFullNames.Contains(definitionName ?? string.Empty))
            {
                return current.GenericTypeArguments.FirstOrDefault();
            }
        }

        return null;
    }

    /// <summary>
    /// Scalar means: primitive, string, or enum. These are leaf nodes in the
    /// query output.
    /// </summary>
    public static bool IsScalar(Type type)
    {
        if (type.IsEnum)
            return true;
        if (type.IsPrimitive)
            return true;
        if (type.FullName == "System.String")
            return true;
        return false;
    }

    /// <summary>
    /// A short, human-readable name for the member's type. Keeps the original
    /// short name for scalars and wrappers, and reduces <c>List&lt;T&gt;</c>
    /// and array types to a compact form.
    /// </summary>
    public string FriendlyName(Type type)
    {
        if (type.IsArray)
            return FriendlyName(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var baseName = definition.Name;
            var tickIndex = baseName.IndexOf('`');
            if (tickIndex >= 0)
                baseName = baseName[..tickIndex];

            if (definition.FullName == Il2CppSystemListFullName || definition.FullName == BclListFullName)
                baseName = "List";
            else if (Il2CppArrayDefinitionFullNames.Contains(definition.FullName ?? string.Empty))
                baseName = "Il2CppArray";

            var args = string.Join(", ", type.GenericTypeArguments.Select(FriendlyName));
            return $"{baseName}<{args}>";
        }

        return type.FullName switch
        {
            "System.Boolean" => "Boolean",
            "System.Byte" => "Byte",
            "System.SByte" => "SByte",
            "System.Int16" => "Int16",
            "System.UInt16" => "UInt16",
            "System.Int32" => "Int32",
            "System.UInt32" => "UInt32",
            "System.Int64" => "Int64",
            "System.UInt64" => "UInt64",
            "System.Single" => "Single",
            "System.Double" => "Double",
            "System.String" => "String",
            _ => type.Name,
        };
    }

    public void Dispose() => _context.Dispose();

    private static bool IsStopBase(Type type)
    {
        if (type == null)
            return true;
        if (type.FullName == Il2CppObjectBaseFullName)
            return true;
        if (type.FullName == "System.Object")
            return true;
        return false;
    }
}

public enum MemberKind
{
    Property,
    Field,
}

public sealed record MemberShape(
    string Name,
    MemberKind Kind,
    Type MemberType,
    string DeclaringTypeFullName,
    bool IsInherited,
    bool IsWritable);
