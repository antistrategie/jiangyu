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
    private const string Il2CppSystemHashSetFullName = "Il2CppSystem.Collections.Generic.HashSet`1";
    private const string BclHashSetFullName = "System.Collections.Generic.HashSet`1";

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
        var primary = context.LoadFromAssemblyPath(assemblyPath);

        // Some asset types (e.g. Stem.SoundBank used for audio routing) live in
        // Assembly-CSharp-firstpass alongside Assembly-CSharp rather than in the
        // primary. Load the firstpass sibling so the catalogue can resolve them
        // too. The allowlist is exact rather than a glob so a stale or backup
        // file (Assembly-CSharp-old.dll, etc.) can't get pulled in and produce
        // duplicate type entries that surface as bogus ambiguities at query time.
        var assembliesToScan = new List<Assembly> { primary };
        var primaryDir = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(primaryDir) && Directory.Exists(primaryDir))
        {
            foreach (var siblingName in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" })
            {
                var dll = Path.Combine(primaryDir, siblingName);
                if (!File.Exists(dll))
                    continue;
                if (IsSameFilePath(dll, assemblyPath))
                    continue;
                try
                {
                    assembliesToScan.Add(context.LoadFromAssemblyPath(dll));
                }
                catch
                {
                    // Non-fatal: a malformed sibling shouldn't prevent the
                    // primary catalogue from loading.
                }
            }
        }

        var allTypes = new List<Type>();
        foreach (var asm in assembliesToScan)
        {
            try
            {
                allTypes.AddRange(asm.GetTypes());
            }
            catch (ReflectionTypeLoadException ex)
            {
                allTypes.AddRange(ex.Types.Where(t => t != null)!);
            }
        }

        return new TemplateTypeCatalog(context, allTypes.ToArray(), supplement);
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
                    NamedArrayEnumTypeName: GetNamedArrayEnumShortName(property),
                    IsOdinHashSet: IsHashSetCollection(property.PropertyType)));
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
                    NamedArrayEnumTypeName: GetNamedArrayEnumShortName(field),
                    IsOdinHashSet: IsHashSetCollection(field.FieldType)));
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
        // having to enumerate every leaf wrapper here. HashSet<T> is treated
        // as a collection too: its runtime instance accepts Add(T)/Remove(T)/
        // Clear() and the visual editor needs the element type to render
        // append/remove rows. Order-based ops (Insert / Set-with-index) are
        // rejected by the validator instead.
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!current.IsGenericType)
                continue;

            var definitionName = current.GetGenericTypeDefinition().FullName;
            if (definitionName == Il2CppSystemListFullName
                || definitionName == BclListFullName
                || definitionName == Il2CppSystemHashSetFullName
                || definitionName == BclHashSetFullName
                || Il2CppArrayDefinitionFullNames.Contains(definitionName ?? string.Empty))
            {
                return current.GenericTypeArguments.FirstOrDefault();
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="type"/> is a (BCL or Il2Cpp) <c>HashSet&lt;T&gt;</c>.
    /// HashSet fields differ from List fields in two ways the editor and
    /// applier care about: they have no positional index (so InsertAt and
    /// Set-with-index are nonsensical), and Remove takes a value rather
    /// than an index. Used by the catalog to set the <c>IsOdinHashSet</c>
    /// member flag and by the validator / applier to switch behaviour.
    /// </summary>
    public static bool IsHashSetCollection(Type type)
        => Jiangyu.Shared.Templates.TemplateTypeRules.IsHashSetCollection(type);

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

    /// <summary>
    /// True when <paramref name="baseType"/> has any concrete strict
    /// descendant — interface impls, abstract-class subtypes, or
    /// reference-style subtypes. Distinct from
    /// <see cref="HasReferenceSubtype"/>, which only counts subtypes
    /// that are template-reference targets (ScriptableObject/DataTemplate).
    /// Used by the navigator to gate polymorphic descent: any abstract /
    /// interface destination with concrete impls needs a subtype hint
    /// before the navigator can resolve subsequent member lookups.
    /// </summary>
    public bool HasPolymorphicSubtype(Type baseType)
    {
        return EnumerateConcreteSubtypes(baseType).Count > 0;
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

        // Interface fallback: Il2CppInterop wraps interfaces as CLASSES
        // (extending Il2CppObjectBase) and strips the implements relations
        // from concrete types' CIL, so `System.Type.IsAssignableFrom` on a
        // wrapped interface returns false for every concrete impl, AND
        // `baseType.IsInterface` itself returns false. The metadata
        // supplement records (concrete, interface) pairs walked from the
        // Cpp2IL-enriched assemblies, which preserve the relationship from
        // global-metadata.dat. Run the supplement lookup unconditionally
        // when a supplement is available; the worst case (baseType isn't
        // actually an interface) is a no-op since no entry matches.
        if (_supplement != null)
        {
            var seen = new HashSet<Type>(subtypes);
            // The supplement walks the unwrapped Cpp2IL types, so it stores
            // names in the form "Menace.Tactical.Skills.MoraleStateCondition".
            // The MetadataLoadContext-loaded Assembly-CSharp.dll exposes the
            // wrapped form "Il2CppMenace.Tactical.Skills.MoraleStateCondition".
            // Look up both shapes — the wrapped one is the one we expect to
            // find at runtime, the unwrapped one is a fallback for any
            // Cpp2IL output that happens to land without the prefix.
            var ifaceShortNames = new[]
            {
                baseType.FullName,
                baseType.FullName?.StartsWith(Il2CppNamespacePrefix, StringComparison.Ordinal) == true
                    ? baseType.FullName![Il2CppNamespacePrefix.Length..]
                    : null,
            };
            foreach (var ifaceLookup in ifaceShortNames)
            {
                if (string.IsNullOrEmpty(ifaceLookup)) continue;
                foreach (var concreteFullName in _supplement.GetInterfaceImplementations(ifaceLookup))
                {
                    var concrete = ResolveSupplementName(concreteFullName);
                    if (concrete is null) continue;
                    if (!seen.Add(concrete)) continue;
                    if (HasStrictDescendant(concrete)) continue;
                    subtypes.Add(concrete);
                }
            }
        }

        return subtypes;
    }

    /// <summary>
    /// Resolve a name string emitted by the metadata supplement to a Type
    /// in the loaded assembly. The supplement walks Cpp2IL output which
    /// uses the unwrapped namespace form; the catalog's MetadataLoadContext
    /// sees the Il2CppInterop-wrapped form. We try both.
    /// </summary>
    private Type? ResolveSupplementName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        var direct = _allTypes.FirstOrDefault(t => t.FullName == fullName);
        if (direct != null) return direct;

        // Inject the Il2Cpp prefix at the namespace root and retry.
        var prefixed = Il2CppNamespacePrefix + fullName;
        return _allTypes.FirstOrDefault(t => t.FullName == prefixed);
    }

    /// <summary>
    /// Resolve a subtype-hint string (short name or fully-qualified name)
    /// constrained to concrete subtypes of <paramref name="baseType"/>.
    /// Returns null when no candidate matches; <paramref name="ambiguousFullNames"/>
    /// is populated only when several subtypes share the requested short
    /// name, so callers can present a hint-narrowed disambiguation error.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResolveType"/>, this never returns a non-subtype:
    /// short-name collisions with classes outside the subtype family (e.g.
    /// <c>Effects.Attack</c> vs the unrelated <c>AI.Behaviors.Attack</c>) are
    /// resolved by ignoring the non-subtype, which is the right behaviour
    /// for both polymorphic descent navigation and handler-construction
    /// validation.
    /// </remarks>
    public Type? ResolveSubtypeHint(
        Type baseType,
        string hint,
        out IReadOnlyList<string> ambiguousFullNames)
    {
        ambiguousFullNames = [];
        if (string.IsNullOrEmpty(hint))
            return null;

        var subtypes = EnumerateConcreteSubtypes(baseType);

        var exact = subtypes.FirstOrDefault(t => t.FullName == hint);
        if (exact != null)
            return exact;

        var shortMatches = subtypes.Where(t => t.Name == hint).ToArray();
        if (shortMatches.Length == 1)
            return shortMatches[0];

        if (shortMatches.Length > 1)
        {
            ambiguousFullNames = shortMatches.Select(t => t.FullName ?? t.Name).ToArray();
            return null;
        }

        return null;
    }

    /// <summary>
    /// Resolve a tagged-string discriminator (the <c>"TYPE"</c> prefix of a
    /// <c>"TYPE|{json}"</c> entry) to the concrete subtype of
    /// <paramref name="baseType"/> it identifies.
    ///
    /// Falls back to <see cref="ResolveSubtypeHint"/> when the discriminator
    /// happens to match a subtype's short name directly. Otherwise generates
    /// candidate discriminators per subtype using the
    /// <c>strip-suffix-of-base</c> + <c>upper</c> + <c>screaming-snake</c>
    /// rules that match MENACE's hand-rolled
    /// <c>ISerializationCallbackReceiver</c> conventions
    /// (<c>BaseConversationNode</c> uses UPPER, <c>BaseRoleRequirement</c>
    /// uses PascalCase, <c>BaseConversationNodeAction</c> uses PascalCase).
    ///
    /// Returns null on no match or ambiguity (caller logs and prompts the
    /// modder to qualify).
    /// </summary>
    public Type? ResolveTaggedDiscriminator(
        Type baseType,
        string discriminator,
        out IReadOnlyList<string> ambiguousFullNames)
    {
        ambiguousFullNames = [];
        if (string.IsNullOrEmpty(discriminator))
            return null;

        // Direct short-name / FQN match comes first — preserves the existing
        // path for modders who write the full class name in composite=.
        var direct = ResolveSubtypeHint(baseType, discriminator, out ambiguousFullNames);
        if (direct != null) return direct;
        if (ambiguousFullNames.Count > 0) return null;

        // Tagged-string subtypes can be non-leaf — a concrete class that
        // itself has further-derived classes (e.g. SayConversationNode is
        // the parent of ChoiceConversationNode but is still concrete and
        // appears as a SAY entry in vanilla data).
        // EnumerateConcreteSubtypes filters out non-leaves, so use the
        // wider non-abstract enumeration here.
        var subtypes = EnumerateNonAbstractSubtypes(baseType);
        if (subtypes.Count == 0) return null;

        // Sampled-discriminator gate: if the asset index recorded vanilla
        // discriminators for this base, reject any candidate not in that
        // set. Otherwise the heuristic accepts forms vanilla's runtime
        // OnAfterDeserialize can't read (e.g. "Action" against
        // ConversationNodeType.ACTION). When no index is installed,
        // fall back to the heuristic so fixture tests without sampling
        // still work.
        var baseFqn = baseType.FullName ?? baseType.Name;
        var allowed = TaggedDiscriminatorIndex.GetAllowed(baseFqn);
        if (allowed is not null && !allowed.Contains(discriminator))
        {
            ambiguousFullNames = allowed.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            return null;
        }

        var matches = new List<Type>();
        foreach (var subtype in subtypes)
        {
            foreach (var candidate in EnumerateDiscriminatorCandidates(baseType, subtype))
            {
                if (string.Equals(candidate, discriminator, StringComparison.Ordinal))
                {
                    matches.Add(subtype);
                    break;
                }
            }
        }

        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
            ambiguousFullNames = matches.Select(t => t.FullName ?? t.Name).ToArray();
        return null;
    }

    /// <summary>
    /// All non-abstract subtypes of <paramref name="baseType"/>, including
    /// those that themselves have descendants. Distinct from
    /// <see cref="EnumerateConcreteSubtypes"/>, which keeps only leaves —
    /// the leaves-only filter is the right choice for polymorphic-
    /// reference-array authoring (where the modder picks a final concrete
    /// type), but the wrong choice for tagged-string discriminators where
    /// a concrete intermediate type is still a valid pick.
    /// </summary>
    private IReadOnlyList<Type> EnumerateNonAbstractSubtypes(Type baseType)
    {
        var subtypes = new List<Type>();
        foreach (var candidate in _allTypes)
        {
            if (candidate == baseType) continue;
            if (candidate.IsAbstract) continue;
            if (!baseType.IsAssignableFrom(candidate)) continue;
            subtypes.Add(candidate);
        }
        return subtypes;
    }

    /// <summary>
    /// Enumerate the candidate discriminator strings for <paramref name="subtype"/>
    /// under the family rooted at <paramref name="baseType"/>. The set covers
    /// the conventions observed in vanilla MENACE:
    /// <list type="bullet">
    /// <item><c>BaseConversationNode</c> → <c>ActionConversationNode</c> →
    /// discriminators <c>"Action"</c> and <c>"ACTION"</c></item>
    /// <item><c>BaseRoleRequirement</c> → <c>HasOneTagRoleRequirement</c> →
    /// discriminator <c>"HasOneTag"</c></item>
    /// <item><c>BaseConversationNodeAction</c> → <c>SetFlagAction</c> →
    /// discriminator <c>"SetFlag"</c> (the family suffix is <c>"Action"</c>,
    /// not the full <c>"ConversationNodeAction"</c> residue)</item>
    /// </list>
    /// The family suffix is computed per subtype as the longest common
    /// suffix between the subtype short name and the base's de-Base'd
    /// residue. This handles the mixed convention where the base residue
    /// names a wider hierarchy than the concrete subtype suffix.
    /// </summary>
    private static IEnumerable<string> EnumerateDiscriminatorCandidates(Type baseType, Type subtype)
    {
        var subtypeShort = subtype.Name;
        yield return subtypeShort;

        // De-Base the base name once. Residue is the candidate "ceiling" of
        // the family suffix; the actual suffix may be shorter per subtype.
        var baseResidue = DeriveBaseResidue(baseType.Name);
        if (string.IsNullOrEmpty(baseResidue)) yield break;

        // Longest common suffix of subtypeShort and baseResidue at PascalCase
        // boundaries — anchor to an uppercase boundary so we don't strip
        // mid-word (e.g. "SeFooAction" vs "ConversationNodeAction" should
        // strip "Action", not "ooAction").
        var commonSuffix = LongestPascalSuffix(subtypeShort, baseResidue);
        if (string.IsNullOrEmpty(commonSuffix)) yield break;
        if (subtypeShort.Length <= commonSuffix.Length) yield break;

        var pascal = subtypeShort[..^commonSuffix.Length];
        yield return pascal;
        yield return pascal.ToUpperInvariant();
        yield return PascalToScreamingSnake(pascal);
    }

    /// <summary>
    /// Strip a leading or trailing <c>Base</c> affix from a base class short
    /// name. Returns the residue used as the ceiling for per-subtype
    /// family-suffix derivation. Empty result means the base name carried
    /// no recognisable affix.
    /// </summary>
    private static string DeriveBaseResidue(string baseShortName)
    {
        const string baseAffix = "Base";
        if (baseShortName.StartsWith(baseAffix, StringComparison.Ordinal)
            && baseShortName.Length > baseAffix.Length)
            return baseShortName[baseAffix.Length..];
        if (baseShortName.EndsWith(baseAffix, StringComparison.Ordinal)
            && baseShortName.Length > baseAffix.Length)
            return baseShortName[..^baseAffix.Length];
        return string.Empty;
    }

    /// <summary>
    /// Longest common suffix between <paramref name="a"/> and
    /// <paramref name="b"/> that begins at a PascalCase boundary inside
    /// <paramref name="a"/>. The boundary anchor keeps the heuristic from
    /// splitting at an arbitrary character index — only at the start of an
    /// upper-case word in the subtype short name.
    /// </summary>
    private static string LongestPascalSuffix(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return string.Empty;

        var limit = Math.Min(a.Length, b.Length);
        var matchLen = 0;
        for (var i = 1; i <= limit; i++)
        {
            if (a[a.Length - i] != b[b.Length - i]) break;
            matchLen = i;
        }
        if (matchLen == 0) return string.Empty;

        // Walk the match back to the nearest PascalCase boundary so we don't
        // strip mid-word. "SetFlagAction" vs "ConversationNodeAction": raw
        // matchLen is "deAction" (7) but only "Action" (6) starts at an
        // upper-case letter; cap at 6.
        var startIndex = a.Length - matchLen;
        while (startIndex < a.Length && !char.IsUpper(a[startIndex]))
            startIndex++;
        if (startIndex >= a.Length) return string.Empty;
        return a[startIndex..];
    }

    private static string PascalToScreamingSnake(string pascal)
    {
        if (string.IsNullOrEmpty(pascal)) return pascal;
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]))
                sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
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
        // Tagged-string detection runs unconditionally — it doesn't depend
        // on the IL2CPP supplement, only on the member list itself. The
        // supplement enrichment below is layered on top.
        var taggedBases = DetectTaggedStringBases(members);

        if (_supplement is null)
            return taggedBases.Count == 0
                ? members
                : ApplyTaggedBases(members, taggedBases);

        if (_supplement.NamedArrays.Count == 0 && _supplement.Fields.Count == 0)
            return taggedBases.Count == 0
                ? members
                : ApplyTaggedBases(members, taggedBases);

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

            if (taggedBases.TryGetValue(current.Name, out var taggedBase))
                current = current with { TaggedPolymorphicBase = taggedBase };

            enriched.Add(current);
        }
        return enriched;
    }

    /// <summary>
    /// Scan a member list for the tagged-string serialisation convention and
    /// return a map from each tagged-string field's name to its polymorphic
    /// base type. Detects two pair shapes that appear consistently across
    /// MENACE's hand-rolled <c>ISerializationCallbackReceiver</c> sites:
    ///
    /// <list type="bullet">
    /// <item><b>List form:</b> <c>m_SerializedX : List&lt;string&gt;</c>
    /// paired with <c>m_X : List&lt;TBase&gt;</c> or
    /// <c>X : List&lt;TBase&gt;</c>. Each stored string is
    /// <c>"DISCRIMINATOR|{json}"</c>. Example:
    /// <c>ConversationNodeContainer.m_SerializedNodes</c> ↔
    /// <c>m_Nodes : List&lt;BaseConversationNode&gt;</c>.</item>
    /// <item><b>Scalar form:</b> <c>m_SerX : string</c> paired with
    /// <c>m_X : TBase</c>. Example:
    /// <c>ActionConversationNode.m_SerAction</c> ↔
    /// <c>m_Action : BaseConversationNodeAction</c>.</item>
    /// </list>
    ///
    /// Detection is structural: the tagged-string sibling must be string-
    /// typed, the typed sibling must hold the corresponding polymorphic
    /// base (list element type or scalar type), and the names must align
    /// via the <c>Ser*</c> / <c>Serialized*</c> prefix-stripping rule.
    /// </summary>
    private static Dictionary<string, Type> DetectTaggedStringBases(IReadOnlyList<MemberShape> members)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        if (members.Count == 0) return result;

        // Index by name for sibling lookup.
        var byName = new Dictionary<string, MemberShape>(StringComparer.Ordinal);
        foreach (var member in members)
            byName[member.Name] = member;

        foreach (var member in members)
        {
            var name = member.Name;
            string? typedSiblingName = null;

            // Recognise the two name shapes:
            //   m_SerializedX → m_X or X
            //   m_SerX        → m_X or X
            // and the unprefixed variants SerializedX → X, SerX → X
            // (the unprefixed forms are uncommon but covered for safety).
            if (name.StartsWith("m_Serialized", StringComparison.Ordinal) && name.Length > "m_Serialized".Length)
                typedSiblingName = name["m_Serialized".Length..];
            else if (name.StartsWith("m_Ser", StringComparison.Ordinal)
                     && name.Length > "m_Ser".Length
                     && char.IsUpper(name["m_Ser".Length]))
                typedSiblingName = name["m_Ser".Length..];
            else if (name.StartsWith("Serialized", StringComparison.Ordinal) && name.Length > "Serialized".Length)
                typedSiblingName = name["Serialized".Length..];
            else if (name.StartsWith("Ser", StringComparison.Ordinal)
                     && name.Length > "Ser".Length
                     && char.IsUpper(name["Ser".Length]))
                typedSiblingName = name["Ser".Length..];

            if (typedSiblingName is null) continue;

            // The tagged-string member itself must be string or List<string>.
            var isListShape = IsStringListShape(member.MemberType);
            var isScalarShape = member.MemberType.FullName == "System.String";
            if (!isListShape && !isScalarShape) continue;

            // Find the typed sibling (try m_-prefixed first, then bare).
            if (!byName.TryGetValue("m_" + typedSiblingName, out var typedSibling)
                && !byName.TryGetValue(typedSiblingName, out typedSibling))
                continue;

            // Extract the polymorphic base type from the typed sibling.
            Type? baseType;
            if (isListShape)
            {
                if (!IsListShape(typedSibling.MemberType)) continue;
                baseType = GetElementType(typedSibling.MemberType);
            }
            else
            {
                // Scalar pair: typed sibling holds the polymorphic value
                // directly. Reject if it's also string-shaped (would be a
                // duplicate marker, not a typed pair).
                if (typedSibling.MemberType.FullName == "System.String") continue;
                baseType = typedSibling.MemberType;
            }

            if (baseType is null || IsScalar(baseType)) continue;

            result[name] = baseType;
        }

        return result;
    }

    /// <summary>
    /// Overlay tagged-base detection results on a member list when the
    /// supplement-driven enrichment is otherwise a no-op. Keeps allocation
    /// to the cases that actually need rewriting.
    /// </summary>
    private static IReadOnlyList<MemberShape> ApplyTaggedBases(
        IReadOnlyList<MemberShape> members,
        IReadOnlyDictionary<string, Type> taggedBases)
    {
        var result = new List<MemberShape>(members.Count);
        foreach (var member in members)
        {
            if (taggedBases.TryGetValue(member.Name, out var baseType))
                result.Add(member with { TaggedPolymorphicBase = baseType });
            else
                result.Add(member);
        }
        return result;
    }

    private static bool IsListShape(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            var definitionName = current.GetGenericTypeDefinition().FullName;
            if (definitionName == Il2CppSystemListFullName || definitionName == BclListFullName)
                return true;
        }
        return false;
    }

    private static bool IsStringListShape(Type type)
    {
        if (!IsListShape(type)) return false;
        var element = GetElementType(type);
        return element?.FullName == "System.String";
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

    /// <summary>
    /// Whether two file-path strings point at the same on-disk file. Both
    /// sides are run through <see cref="Path.GetFullPath(string)"/> so
    /// separator-mix (<c>D:\foo\bar/Assembly-CSharp.dll</c> on Windows, from
    /// <see cref="Path.Combine(string, string)"/> stitching a backslash base
    /// to a forward-slash relative path) collapses to the same canonical form,
    /// and compared with <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// because Windows filesystems are case-insensitive. Without this, the
    /// sibling-assembly loader could re-load the primary as if it were a
    /// distinct assembly, duplicating every type in the catalogue.
    /// </summary>
    internal static bool IsSameFilePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
    bool IsSoundIdField = false,
    /// <summary>True when the field's declared type is a
    /// <c>HashSet&lt;T&gt;</c>. Triggers a different op contract from
    /// list-shaped collections: Append maps to <c>Add</c> (idempotent),
    /// Remove takes a value (not an index), and InsertAt / Set-with-index
    /// are rejected because HashSet has no order. Always paired with
    /// <see cref="IsLikelyOdinOnly"/> = true since Unity can't natively
    /// serialise HashSet.</summary>
    bool IsOdinHashSet = false,
    /// <summary>
    /// Non-null when this field is a tagged-string serialisation of
    /// polymorphic typed values. The declared <see cref="MemberType"/> is
    /// <c>System.String</c> (scalar form) or <c>List&lt;string&gt;</c>
    /// (list form), while each stored string follows the format
    /// <c>"DISCRIMINATOR|{json}"</c>. This field carries the polymorphic
    /// base whose concrete subtypes form the discriminator namespace
    /// (e.g. <c>BaseConversationNode</c> for
    /// <c>ConversationNodeContainer.m_SerializedNodes</c>). Detected via
    /// the <c>m_Ser*</c> ↔ typed-sibling convention at catalogue build.
    /// Authoring uses the existing <c>composite="X"</c> op against the
    /// tagged-string field; emitter packs to the storage shape.
    /// </summary>
    Type? TaggedPolymorphicBase = null);
