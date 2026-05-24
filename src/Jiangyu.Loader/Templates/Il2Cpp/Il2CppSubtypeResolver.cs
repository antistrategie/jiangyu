namespace Jiangyu.Loader.Templates;

/// <summary>
/// Resolves the Il2CppInterop wrapper type for a modder-supplied short
/// name, scoped to the namespace of a parent element type. Used by the
/// patch applier when a descent block (<c>type="Subtype"</c>) names a
/// polymorphic concrete type that must be assignable to the descent
/// element's static type.
///
/// <para>Same-namespace lookup is tried first because almost every game
/// subtype lives in the same namespace as its base. Falls back to a global
/// short-name search across all loaded assemblies. Results are cached by
/// <c>(namespace, shortName)</c>.</para>
/// </summary>
internal static class Il2CppSubtypeResolver
{
    private static readonly Dictionary<string, Type> Cache = new(StringComparer.Ordinal);

    public static Type Resolve(Type elementType, string shortName)
    {
        var ns = elementType.Namespace ?? string.Empty;
        var cacheKey = ns + "::" + shortName;
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Same-namespace lookup first. Wrap in try/catch so a partial-load
        // assembly doesn't bypass the global fallback. Match by name AND
        // assignability: otherwise an unrelated same-namespace type (e.g.
        // SkillGroup in the same namespace as SkillEventHandlerTemplate)
        // wins the fast path and the caller sees a misleading "type X does
        // not derive from base" error downstream.
        Type same = null;
        try
        {
            same = elementType.Assembly
                .GetTypes()
                .FirstOrDefault(t => t.Name == shortName
                    && t.Namespace == ns
                    && Il2CppTypeAssignability.IsAssignableFromIl2Cpp(elementType, t));
        }
        catch { /* fall through */ }

        if (same != null)
        {
            Cache[cacheKey] = same;
            return same;
        }

        // Fall back to all loaded assemblies: match short name + assignable
        // to the element type. Slower but covers types declared elsewhere.
        Type anywhere = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var candidate in types)
            {
                if (candidate.Name != shortName) continue;
                if (!Il2CppTypeAssignability.IsAssignableFromIl2Cpp(elementType, candidate)) continue;
                anywhere = candidate;
                break;
            }
            if (anywhere != null) break;
        }

        Cache[cacheKey] = anywhere;
        return anywhere;
    }
}
