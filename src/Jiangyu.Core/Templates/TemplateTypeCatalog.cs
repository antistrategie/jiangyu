using System.Reflection;
using Jiangyu.Core.Il2Cpp;

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
    private readonly Type[] _allTypes;
    private readonly Il2CppMetadataSupplement? _supplement;

    private TemplateTypeCatalog(MetadataLoadContext context, Type[] allTypes, Il2CppMetadataSupplement? supplement)
    {
        _context = context;
        _allTypes = allTypes;
        _supplement = supplement;
    }

    public static TemplateTypeCatalog Load(
        string assemblyPath,
        IEnumerable<string>? additionalSearchDirectories = null,
        Il2CppMetadataSupplement? supplement = null)
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

        return new TemplateTypeCatalog(context, allTypes, supplement);
    }

    /// <summary>
    /// Resolve <paramref name="nameOrFullName"/> to a non-abstract type in the
    /// assembly. Accepts both short names (<c>EntityTemplate</c>) and
    /// fully-qualified names (<c>Il2CppMenace.Tactical.EntityTemplate</c>).
    /// Populates <paramref name="ambiguousCandidates"/> when a short name
    /// matches multiple types and no <paramref name="namespaceHint"/> narrows
    /// the choice.
    /// </summary>
    /// <param name="namespaceHint">
    /// Optional CLR namespace of the source type (as the script asset reports
    /// it, e.g. <c>Menace.Tactical.Skills.Effects</c>). Compared against each
    /// candidate's <see cref="Type.Namespace"/>, with Il2CppInterop's
    /// <c>Il2Cpp</c> prefix stripped from the candidate before comparing
    /// because the script asset stores the unwrapped form. Ignored on the
    /// FQN-match path: callers that already have a fully-qualified name take
    /// precedence over any hint.
    /// </param>
    public Type? ResolveType(
        string nameOrFullName,
        out IReadOnlyList<Type> ambiguousCandidates,
        out string? error,
        string? namespaceHint = null)
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
            // FQN match wins; a stale or wrong hint is silently accepted here
            // by design (the caller asked for a specific FullName).
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
            if (!string.IsNullOrWhiteSpace(namespaceHint))
            {
                var hintMatches = shortMatches
                    .Where(t => NamespaceMatchesHint(t.Namespace, namespaceHint))
                    .ToArray();

                if (hintMatches.Length == 1)
                {
                    error = null;
                    return hintMatches[0];
                }

                if (hintMatches.Length > 1)
                {
                    ambiguousCandidates = hintMatches;
                    error = $"type name '{nameOrFullName}' is ambiguous even within namespace '{namespaceHint}'.";
                    return null;
                }
            }

            ambiguousCandidates = shortMatches;
            error = $"type name '{nameOrFullName}' is ambiguous.";
            return null;
        }

        error = $"no type '{nameOrFullName}' found in the assembly.";
        return null;
    }

    private const string Il2CppNamespacePrefix = "Il2Cpp";

    private static bool NamespaceMatchesHint(string? candidateNamespace, string namespaceHint)
    {
        if (string.IsNullOrEmpty(candidateNamespace))
            return false;
        if (string.Equals(candidateNamespace, namespaceHint, StringComparison.Ordinal))
            return true;

        // Il2CppInterop wraps native types under an Il2Cpp-prefixed namespace
        // (e.g. Il2CppMenace.Tactical.Skills.Effects), but the script asset
        // reports the unwrapped form (Menace.Tactical.Skills.Effects). Strip
        // the prefix once and re-compare so the index entry's hint still
        // matches the wrapped candidate.
        if (candidateNamespace.StartsWith(Il2CppNamespacePrefix, StringComparison.Ordinal))
        {
            var unwrapped = candidateNamespace[Il2CppNamespacePrefix.Length..];
            if (string.Equals(unwrapped, namespaceHint, StringComparison.Ordinal))
                return true;
        }

        return false;
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
                    IsWritable: writable,
                    IsLikelyOdinOnly: IsLikelyOdinOnly(property),
                    NamedArrayEnumTypeName: GetNamedArrayEnumShortName(property)));
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
                    IsWritable: writable,
                    IsLikelyOdinOnly: IsLikelyOdinOnly(field),
                    NamedArrayEnumTypeName: GetNamedArrayEnumShortName(field)));
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
    /// True when the assembly contains at least one strict descendant of
    /// <paramref name="baseType"/> that is itself a reference target. Used to
    /// detect polymorphic destinations (e.g. <c>BaseItemTemplate</c> with
    /// <c>WeaponTemplate</c>/<c>ArmorTemplate</c>/... subtypes) — the modder
    /// has to pick a concrete type because <c>DataTemplateLoader</c>'s
    /// <c>m_TemplateMaps</c> is keyed by concrete type, not by inheritance.
    /// Doesn't rely on <see cref="Type.IsAbstract"/>: Il2CppInterop wrappers
    /// don't preserve the abstract bit, so the structural check is the only
    /// reliable polymorphism signal across the IL2CPP boundary.
    /// </summary>
    public bool HasReferenceSubtype(Type baseType)
    {
        foreach (var candidate in _allTypes)
        {
            if (candidate == baseType) continue;
            if (!baseType.IsAssignableFrom(candidate)) continue;
            if (IsTemplateReferenceTarget(candidate)) return true;
        }
        return false;
    }

    public static bool IsTemplateReferenceTarget(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, "UnityEngine.ScriptableObject", StringComparison.Ordinal)
                || string.Equals(current.FullName, "Menace.Tools.DataTemplate", StringComparison.Ordinal)
                || string.Equals(current.Name, "DataTemplate", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the type descends from <c>Menace.Tools.DataTemplate</c>.
    /// Distinguishes ref-style polymorphism (the modder picks an existing
    /// DataTemplateLoader-registered instance, e.g. <c>BaseItemTemplate</c>)
    /// from construction-style polymorphism (the modder constructs a fresh
    /// owned ScriptableObject, e.g. <c>BaseEventHandlerTemplate</c>).
    /// </summary>
    public static bool IsDataTemplateType(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, "Menace.Tools.DataTemplate", StringComparison.Ordinal)
                || string.Equals(current.Name, "DataTemplate", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Concrete strict descendants of <paramref name="baseType"/> the modder
    /// can pick when constructing a polymorphic-list element. "Concrete" here
    /// means leaf in the loaded type graph (no further descendants in the
    /// assembly). IL2CPP wrappers strip <see cref="Type.IsAbstract"/>, so the
    /// structural leaf test is the only reliable filter — an intermediate
    /// abstract base would itself have descendants and gets pruned.
    /// </summary>
    public IReadOnlyList<Type> EnumerateConcreteSubtypes(Type baseType)
    {
        var subtypes = new List<Type>();
        foreach (var candidate in _allTypes)
        {
            if (candidate == baseType) continue;
            if (!baseType.IsAssignableFrom(candidate)) continue;
            if (HasStrictDescendant(candidate)) continue;
            subtypes.Add(candidate);
        }
        return subtypes;
    }

    private bool HasStrictDescendant(Type type)
    {
        foreach (var candidate in _allTypes)
        {
            if (candidate == type) continue;
            if (type.IsAssignableFrom(candidate)) return true;
        }
        return false;
    }

    public static IReadOnlyList<string> GetEnumMemberNames(Type type)
    {
        if (!type.IsEnum)
        {
            return [];
        }

        return
        [
            .. type
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field => field.Name)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
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

        // Non-generic Il2Cpp array wrapper (currently just Il2CppStringArray).
        if (!type.IsGenericType && Il2CppArrayDefinitionFullNames.Contains(type.FullName ?? string.Empty))
            return "String[]";

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var fullName = definition.FullName ?? string.Empty;

            // Il2Cpp array wrappers are interop-level types with the same
            // semantics (and same patch syntax) as the native primitive/ref
            // array they wrap — render them as `T[]` so the UI doesn't leak
            // implementation-level naming to modders.
            if (Il2CppArrayDefinitionFullNames.Contains(fullName))
            {
                var inner = type.GenericTypeArguments.Length == 1
                    ? FriendlyName(type.GenericTypeArguments[0])
                    : "object";
                return inner + "[]";
            }

            var baseName = definition.Name;
            var tickIndex = baseName.IndexOf('`');
            if (tickIndex >= 0)
                baseName = baseName[..tickIndex];

            if (fullName == Il2CppSystemListFullName || fullName == BclListFullName)
                baseName = "List";

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

    /// <summary>
    /// Returns the member list with attribute-derived hints overlaid from the
    /// IL2CPP metadata supplement (e.g. <c>NamedArrayEnumTypeName</c>). When
    /// no supplement is loaded, the input list is returned unchanged.
    /// Members are matched by declaring-type short name + member name —
    /// FullName matching breaks down when comparing Il2CppInterop wrappers
    /// (`Il2Cpp…`-prefixed) against Cpp2IL output (raw names).
    /// </summary>
    public IReadOnlyList<MemberShape> EnrichMembers(Type declaringType, IReadOnlyList<MemberShape> members)
    {
        if (_supplement is null) return members;
        if (_supplement.NamedArrays.Count == 0 && _supplement.Fields.Count == 0) return members;

        var rootShortName = declaringType.Name;
        var enriched = new List<MemberShape>(members.Count);
        foreach (var member in members)
        {
            var current = member;

            // Use the member's own declaring type short name so inherited
            // fields (e.g. Rarity declared on BaseItemTemplate) match the
            // supplement entry keyed under their actual declaring type.
            var memberDeclarerShort = ShortNameFromFull(member.DeclaringTypeFullName);

            // NamedArray pairing — try root type first (declared directly),
            // then fall back to the member's own declaring type.
            if (current.NamedArrayEnumTypeName is null)
            {
                bool found = _supplement.TryFindNamedArrayEnum(rootShortName, current.Name, out var enumName);
                if (!found)
                    found = _supplement.TryFindNamedArrayEnum(memberDeclarerShort, current.Name, out enumName);
                if (found && enumName is not null)
                    current = current with { NamedArrayEnumTypeName = enumName };
            }

            // Per-field attribute hints (Range/Min/Tooltip/HideInInspector/SoundID).
            var meta = _supplement.FindFieldMetadata(rootShortName, current.Name)
                        ?? _supplement.FindFieldMetadata(memberDeclarerShort, current.Name);
            if (meta is not null)
            {
                current = current with
                {
                    NumericMin = meta.RangeMin ?? meta.MinValue ?? current.NumericMin,
                    NumericMax = meta.RangeMax ?? current.NumericMax,
                    Tooltip = meta.Tooltip ?? current.Tooltip,
                    IsHiddenInInspector = current.IsHiddenInInspector || meta.HideInInspector == true,
                    IsSoundIdField = current.IsSoundIdField || meta.IsSoundId == true,
                };
            }

            enriched.Add(current);
        }
        return enriched;
    }

    /// <summary>
    /// Extracts the short type name from a full name like
    /// <c>Il2CppMenace.Items.BaseItemTemplate</c>.
    /// </summary>
    private static string ShortNameFromFull(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullName.Length - 1
            ? fullName[(lastDot + 1)..]
            : fullName;
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

    private static bool IsLikelyOdinOnly(MemberInfo member)
    {
        if (HasOdinSerializeAttribute(member))
            return true;

        Type? memberType = member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _ => null,
        };

        if (memberType is null) return false;
        if (IsNotUnitySerialisable(memberType)) return true;

        // Backstop for cases the structural checks miss: Il2CppInterop wraps
        // native IL2CPP interfaces in ways that don't always set IsInterface
        // / IsAbstract on the CIL side, so a type-shaped check (zero writable
        // own members + isn't a scalar / Unity-object / collection) catches
        // ITacticalCondition / IValueProvider / similar shells. False
        // positives on genuinely empty composites are rare in MENACE; the
        // worst case is a real composite incorrectly flagged, which only
        // hides it from the FieldAdder dropdown — modder can still hand-edit.
        if (LooksLikeUnfillableShell(memberType)) return true;

        return false;
    }

    private static bool LooksLikeUnfillableShell(Type type)
    {
        if (IsScalar(type)) return false;
        if (DescendsFromUnityObject(type)) return false;
        if (GetElementType(type) is not null) return false;
        if (HasAnyWritableMembers(type)) return false;
        return true;
    }

    private static bool HasAnyWritableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        try
        {
            for (var current = type; current != null && !IsStopBase(current); current = current.BaseType)
            {
                foreach (var p in current.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length == 0 && p.CanWrite) return true;
                }
                foreach (var f in current.GetFields(flags))
                {
                    if (!f.IsInitOnly && !f.IsLiteral) return true;
                }
            }
        }
        catch
        {
            // Reflection can throw on partially-loaded IL2CPP types; treat
            // as "we don't know", which lands on false (no members) below
            // and routes through the unfillable-shell path. That's the same
            // outcome the catalog wants — if we can't read the type, the
            // modder can't fill it via reflection-based patching either.
            return false;
        }

        return false;
    }

    private static bool HasOdinSerializeAttribute(MemberInfo member)
    {
        try
        {
            return CustomAttributeData
                .GetCustomAttributes(member)
                .Any(attribute =>
                    string.Equals(attribute.AttributeType.FullName, "Sirenix.Serialization.OdinSerializeAttribute", StringComparison.Ordinal)
                    || string.Equals(attribute.AttributeType.FullName, "Sirenix.OdinInspector.OdinSerializeAttribute", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detects the game's <c>[NamedArray(typeof(X))]</c> convention — a
    /// primitive-element array whose slots correspond 1:1 to the members of
    /// an enum. Matches on attribute *shape* rather than name: any attribute
    /// on a primitive-element array member that carries <c>typeof(SomeEnum)</c>
    /// as a constructor argument is treated as the paired-enum declaration.
    /// Shape-based matching means this keeps working if the game renames the
    /// attribute, moves its namespace, or if Il2CppInterop wraps/mangles the
    /// attribute type.
    /// </summary>
    private static string? GetNamedArrayEnumShortName(MemberInfo member)
    {
        try
        {
            var memberType = member switch
            {
                FieldInfo f => f.FieldType,
                PropertyInfo p => p.PropertyType,
                _ => null,
            };
            if (memberType is null) return null;

            // Only primitive-element collections (byte[], int[], Il2CppStructArray<byte>, …)
            // are plausibly enum-indexed. Reject non-collection members and
            // reference arrays to avoid matching unrelated attributes that
            // happen to take a typeof(enum) argument.
            var elementType = GetElementType(memberType);
            if (elementType is null || !IsScalar(elementType) || elementType.IsEnum) return null;

            foreach (var attribute in CustomAttributeData.GetCustomAttributes(member))
            {
                foreach (var arg in attribute.ConstructorArguments)
                {
                    if (arg.Value is Type enumType && enumType.IsEnum)
                        return enumType.Name;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detects member types that Unity's native serialiser cannot handle.
    /// These are routed through Odin Serializer (Sirenix) via the
    /// <c>serializationData</c> blob. The field exists at runtime (Odin
    /// populates it on load) but is absent from Unity asset data.
    /// </summary>
    private static bool IsNotUnitySerialisable(Type type)
    {
        if (type.IsInterface)
            return true;

        if (type.IsAbstract && !DescendsFromUnityObject(type))
            return true;

        if (IsNonUnitySerialisableCollection(type))
            return true;

        var elementType = GetElementType(type);
        if (elementType != null)
            return IsNotUnitySerialisable(elementType);

        return false;
    }

    private static bool DescendsFromUnityObject(Type type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            var fullName = current.FullName;
            if (string.Equals(fullName, "UnityEngine.Object", StringComparison.Ordinal)
                || string.Equals(fullName, "UnityEngine.ScriptableObject", StringComparison.Ordinal)
                || string.Equals(fullName, "UnityEngine.MonoBehaviour", StringComparison.Ordinal)
                || string.Equals(fullName, "UnityEngine.Component", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> NonUnitySerialisableGenericDefinitions =
        new(StringComparer.Ordinal)
        {
            "System.Collections.Generic.HashSet`1",
            "System.Collections.Generic.Dictionary`2",
            "Il2CppSystem.Collections.Generic.HashSet`1",
            "Il2CppSystem.Collections.Generic.Dictionary`2",
        };

    private static bool IsNonUnitySerialisableCollection(Type type)
    {
        if (!type.IsGenericType)
            return false;

        var definitionName = type.GetGenericTypeDefinition().FullName;
        return NonUnitySerialisableGenericDefinitions.Contains(definitionName ?? string.Empty);
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
    bool IsWritable,
    bool IsLikelyOdinOnly = false,
    /// <summary>
    /// When the member is decorated with <c>[NamedArray(typeof(T))]</c> (a
    /// game convention that binds a primitive-array field to a specific enum
    /// — each slot corresponds to one enum member), this is the short name of
    /// the paired enum type. Consumers treat these arrays as fixed-size
    /// lookups keyed by the enum, not as growable lists.
    /// </summary>
    string? NamedArrayEnumTypeName = null,
    /// <summary>Inclusive lower bound from <c>[Range]</c> or <c>[Min]</c>.</summary>
    double? NumericMin = null,
    /// <summary>Inclusive upper bound from <c>[Range]</c>.</summary>
    double? NumericMax = null,
    /// <summary>Hover hint from <c>[Tooltip]</c>.</summary>
    string? Tooltip = null,
    /// <summary>True when the field carries <c>[HideInInspector]</c>.
    /// Modder-facing UI hides these by default — the game itself keeps them
    /// out of the Unity inspector for a reason.</summary>
    bool IsHiddenInInspector = false,
    /// <summary>True when the field carries the game's SoundID marker
    /// (<c>Stem.SoundIDAttribute</c>). Field type is <c>Stem.ID</c>; UI may
    /// label these or eventually offer a sound-bus picker.</summary>
    bool IsSoundIdField = false);
