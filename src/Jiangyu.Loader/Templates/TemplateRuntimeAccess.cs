using System.Reflection;
using Il2CppInterop.Runtime;
using Jiangyu.Loader.Sdk;
using Jiangyu.Loader.Runtime;
using Jiangyu.Loader.Sdk.Types;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;
using EntityTemplate = Il2CppMenace.Tactical.EntityTemplate;
using Il2CppEnumerable = Il2CppSystem.Collections.IEnumerable;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Generic runtime access to live DataTemplate instances of any type registered
/// with <c>DataTemplateLoader</c>. Centralises reflective invocation of
/// <c>DataTemplateLoader.GetAll&lt;T&gt;()</c>, Il2Cpp collection materialisation,
/// and m_ID identity reads. Consumed by <see cref="TemplatePatchApplier"/> and
/// available as a helper for diagnostics.
/// </summary>
internal static class TemplateRuntimeAccess
{
    // JIANGYU-CONTRACT: Live template identity is the serialised m_ID string,
    // inherited from the DataTemplate base. Scope validated for EntityTemplate
    // via the 2026-04-19 MissionPreparation dump (260 templates, all with
    // m_ID, unique IDs). Each DataTemplate subtype is assumed to follow the
    // same m_ID convention; patch-time logging surfaces any template that
    // lacks a readable m_ID so modders can tell when a given subtype diverges.
    private static readonly string[] IdMemberCandidates = { "m_ID", "ID", "Id", "id" };

    public const string DefaultTemplateTypeName = nameof(EntityTemplate);

    // Memoised name -> Type resolutions. ResolveTemplateType is called once per
    // composite construction, and a miss in the primary assembly walks every
    // loaded assembly calling GetTypes() on each — several ms a call with the
    // ~440 assemblies Il2CppInterop generates. Types whose declaring assembly
    // is not the primary one (Stem.Sound and Stem.SoundVariation are the hot
    // pair: one composite per voice line) hit that walk on every construction.
    //
    // Only successful resolutions are cached: a name that fails to resolve now
    // may resolve later once a mod assembly loads, so caching the failure would
    // freeze a transient miss. Loading a new assembly can also make a
    // previously unique short name ambiguous, so the cache is dropped whenever
    // the assembly set grows. AssemblyLoad can fire on any thread, so reads and
    // writes both take the lock rather than risk tearing the dictionary.
    private static readonly Dictionary<string, Type> ResolvedTypeCache = new(StringComparer.Ordinal);
    private static readonly object ResolvedTypeCacheGate = new();
    private static bool _assemblyLoadHooked;

    // Subscribed under the same gate that guards the cache, so no thread can
    // observe a live cache that nothing invalidates.
    private static void EnsureAssemblyLoadHook()
    {
        lock (ResolvedTypeCacheGate)
        {
            if (_assemblyLoadHooked)
                return;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
            _assemblyLoadHooked = true;
        }
    }

    private static void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
    {
        lock (ResolvedTypeCacheGate)
            ResolvedTypeCache.Clear();
    }

    private static bool TryGetCachedType(string templateTypeName, out Type cached)
    {
        lock (ResolvedTypeCacheGate)
            return ResolvedTypeCache.TryGetValue(templateTypeName, out cached);
    }

    private static Type CacheResolvedType(string templateTypeName, Type resolved)
    {
        lock (ResolvedTypeCacheGate)
            ResolvedTypeCache[templateTypeName] = resolved;
        return resolved;
    }

    /// <summary>
    /// Looks up a single live template by its identity string. Dispatches by
    /// base class:
    /// <list type="bullet">
    ///   <item><term>DataTemplate subtypes</term><description>
    ///     Resolve via <c>DataTemplateLoader.TryGet&lt;T&gt;(m_ID)</c>. Sees
    ///     both game-native templates and Jiangyu-registered clones (both
    ///     live in <c>m_TemplateMaps</c>). The identity is the template's
    ///     serialised <c>m_ID</c>.</description></item>
    ///   <item><term>Other ScriptableObject subtypes</term><description>
    ///     Resolve via <c>Resources.FindObjectsOfTypeAll</c> filtered by
    ///     <c>Object.name</c>. Identity for these is the asset's
    ///     <c>m_Name</c> — they don't inherit from <c>DataTemplate</c> and
    ///     aren't in <c>DataTemplateLoader</c>'s registry.</description></item>
    /// </list>
    /// </summary>
    public static bool TryGetTemplateById(
        string templateTypeName, string templateId,
        out Il2CppObjectBase template, out Type resolvedType, out string error)
    {
        template = null;
        resolvedType = null;

        if (string.IsNullOrWhiteSpace(templateTypeName))
        {
            error = "template type name is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(templateId))
        {
            error = "template id is empty.";
            return false;
        }

        resolvedType = ResolveTemplateType(templateTypeName, out error);
        if (resolvedType == null)
            return false;

        return TryGetTemplateById(resolvedType, templateId, out template, out error);
    }

    /// <summary>
    /// Looks up a single live template by its identity string against a known
    /// concrete type, dispatching by base class exactly as the type-name
    /// overload does. Used when the caller already holds the target type (e.g.
    /// marshalling a verb argument) and so needs no name resolution.
    /// </summary>
    public static bool TryGetTemplateById(
        Type resolvedType, string templateId,
        out Il2CppObjectBase template, out string error)
    {
        template = null;

        if (resolvedType == null)
        {
            error = "template type is null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(templateId))
        {
            error = "template id is empty.";
            return false;
        }

        if (typeof(DataTemplate).IsAssignableFrom(resolvedType))
            return TryResolveDataTemplate(templateId, resolvedType, out template, out error);

        if (typeof(ScriptableObject).IsAssignableFrom(resolvedType))
            return TryResolveScriptableObjectByName(templateId, resolvedType, out template, out error);

        error = $"template type {resolvedType.FullName} is neither DataTemplate nor ScriptableObject.";
        return false;
    }

    private static bool TryResolveDataTemplate(
        string templateId, Type resolvedType, out Il2CppObjectBase template, out string error)
    {
        template = null;
        var tryGet = typeof(DataTemplateLoader)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "TryGet"
                                 && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 2);

        if (tryGet == null)
        {
            error = "DataTemplateLoader.TryGet<T>(string, out T) not found.";
            return false;
        }

        var args = new object[] { templateId, null };
        bool found;
        try
        {
            found = (bool)tryGet.MakeGenericMethod(resolvedType).Invoke(null, args);
        }
        catch (Exception ex)
        {
            error = $"TryGet<{resolvedType.Name}> threw: {ex.Message}";
            return false;
        }

        if (!found || args[1] is not Il2CppObjectBase resolved)
        {
            error = null;
            return false;
        }

        template = resolved;
        error = null;
        return true;
    }

    private static bool TryResolveScriptableObjectByName(
        string templateId, Type resolvedType, out Il2CppObjectBase template, out string error)
    {
        error = null;
        if (TryFindScriptableObjectByName(templateId, resolvedType, out template))
            return true;

        // A miss may be an asset the game has unloaded since its folder was last
        // loaded, so load the folder again and scan once more before giving up.
        return EnsureResourcesFolderLoaded(templateTypeName: null, resolvedType, reload: true)
            && TryFindScriptableObjectByName(templateId, resolvedType, out template);
    }

    private static bool TryFindScriptableObjectByName(string templateId, Type resolvedType, out Il2CppObjectBase template)
    {
        template = null;
        var il2CppType = Il2CppType.From(resolvedType);
        var candidates = Resources.FindObjectsOfTypeAll(il2CppType);
        if (candidates == null || candidates.Length == 0)
            return false;

        // FindObjectsOfTypeAll returns UnityEngine.Object base wrappers; cast
        // to the specific resolved type so consumers storing into a typed
        // array (e.g. Il2CppReferenceArray<PerkTreeTemplate>) get a wrapper
        // of the correct element type.
        var tryCast = Il2CppReflectiveCast.GetTryCastMethod(resolvedType);
        foreach (var candidate in candidates)
        {
            if (candidate == null)
                continue;
            if (!string.Equals(candidate.name, templateId, StringComparison.Ordinal))
                continue;

            template = tryCast.Invoke(candidate, null) as Il2CppObjectBase;
            return template != null;
        }

        return false;
    }

    public static bool IsDataTemplateType(Type resolvedType)
        => resolvedType != null && typeof(DataTemplate).IsAssignableFrom(resolvedType);

    public static bool IsScriptableObjectType(Type resolvedType)
        => resolvedType != null && typeof(ScriptableObject).IsAssignableFrom(resolvedType);

    /// <summary>Whether two type names address the same templates: the same spelling, or
    /// types one of which derives from the other, so an id looked up under either finds the
    /// same object (a DataTemplate is registered in every ancestor map, a ScriptableObject is
    /// found by name under any base type). <c>DataTemplate</c> itself has no map and matches
    /// only its own spelling.</summary>
    public static bool SameTemplateSpace(string typeNameA, string typeNameB)
    {
        if (string.IsNullOrEmpty(typeNameA) || string.IsNullOrEmpty(typeNameB))
            return false;
        if (string.Equals(typeNameA, typeNameB, StringComparison.Ordinal))
            return true;
        var a = ResolveTemplateType(typeNameA, out _);
        var b = ResolveTemplateType(typeNameB, out _);
        if (a == null || b == null || a == typeof(DataTemplate) || b == typeof(DataTemplate))
            return false;
        return a.IsAssignableFrom(b) || b.IsAssignableFrom(a);
    }

    // The canonical name of each spelling seen. A name that does not resolve is not cached:
    // its type may be injected later.
    private static readonly Dictionary<string, string> CanonicalNames = new(StringComparer.Ordinal);

    /// <summary>The one name the loader files a type under, whatever spelling a mod used: a
    /// code-defined <c>ns:Name</c> as written; otherwise the resolved type's short name when
    /// that short name resolves to the same type on its own, else its full name; a name that
    /// does not resolve, as written. Both catalogues, the held blocks, the changed sets and
    /// the missing-template keys use it, so a qualified spelling, a short spelling and a clone
    /// directive name one thing.</summary>
    public static string CanonicalTypeName(string templateTypeName)
    {
        if (string.IsNullOrEmpty(templateTypeName) || templateTypeName.Contains(':'))
            return templateTypeName;
        if (CanonicalNames.TryGetValue(templateTypeName, out var canonical))
            return canonical;
        var type = ResolveTemplateType(templateTypeName, out _);
        if (type == null)
            return templateTypeName;
        canonical = string.Equals(type.Name, templateTypeName, StringComparison.Ordinal) || ResolveTemplateType(type.Name, out _) == type
            ? type.Name
            : type.FullName ?? templateTypeName;
        CanonicalNames[templateTypeName] = canonical;
        return canonical;
    }

    /// <summary>Loads the type's Resources folder again and enumerates its live objects. A
    /// probe that missed calls this once per type per scene: the game unloads an asset nothing
    /// references, and the reload brings it back.</summary>
    public static IReadOnlyList<Il2CppObjectBase> ReloadScriptableObjects(string templateTypeName, Type resolvedType)
    {
        EnsureResourcesFolderLoaded(templateTypeName, resolvedType, reload: true);
        return EnumerateScriptableObjects(resolvedType);
    }

    /// <summary>Whether a type name names the SoundBank type, short or qualified.</summary>
    public static bool IsSoundBankTypeName(string templateTypeName)
        => string.Equals(templateTypeName, "SoundBank", StringComparison.Ordinal)
            || (templateTypeName != null && templateTypeName.EndsWith(".SoundBank", StringComparison.Ordinal));

    /// <summary>Finds a ScriptableObject template by <c>Object.name</c> in a list
    /// <see cref="GetAllTemplates"/> returned, so a caller with several ids to check
    /// walks the loaded objects once.</summary>
    public static bool TryFindByName(IReadOnlyList<Il2CppObjectBase> candidates, string templateId, out Il2CppObjectBase template)
    {
        template = null;
        if (candidates == null || string.IsNullOrEmpty(templateId))
            return false;

        foreach (var candidate in candidates)
        {
            if (candidate is UnityEngine.Object obj && string.Equals(obj.name, templateId, StringComparison.Ordinal))
            {
                template = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns all live templates of the given type, dispatching by base class:
    /// <list type="bullet">
    ///   <item><description>DataTemplate subtypes: enumerated via
    ///     <c>DataTemplateLoader.GetAll&lt;T&gt;()</c>. Materialises the cache
    ///     on first call. Empty return means "not ready yet" — callers retry.</description></item>
    ///   <item><description>Other ScriptableObject subtypes (e.g.
    ///     PerkTreeTemplate): enumerated via
    ///     <c>Resources.FindObjectsOfTypeAll</c>. Always returns the current
    ///     set immediately; an empty return means no assets of this type are
    ///     loaded (not "not ready yet").</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<Il2CppObjectBase> GetAllTemplates(string templateTypeName, out Type resolvedType, out string resolveError, bool reloadOnEmpty = true)
    {
        resolvedType = null;
        if (string.IsNullOrWhiteSpace(templateTypeName))
        {
            resolveError = "template type name is empty.";
            return Array.Empty<Il2CppObjectBase>();
        }

        var type = ResolveTemplateType(templateTypeName, out resolveError);
        if (type == null)
            return Array.Empty<Il2CppObjectBase>();

        resolvedType = type;

        if (typeof(DataTemplate).IsAssignableFrom(type))
        {
            var collection = TryInvokeGetAll(type);
            return collection == null
                ? Array.Empty<Il2CppObjectBase>()
                : MaterialiseTemplates(collection, type);
        }

        if (typeof(ScriptableObject).IsAssignableFrom(type))
        {
            EnsureResourcesFolderLoaded(templateTypeName, type);
            // An empty enumeration may mean the game unloaded the type's assets: one reload,
            // unless the caller bounds reloads itself (the patch applier's probe, once per
            // type per scene).
            var live = EnumerateScriptableObjects(type);
            if (live.Count == 0 && reloadOnEmpty && EnsureResourcesFolderLoaded(templateTypeName, type, reload: true))
                live = EnumerateScriptableObjects(type);
            return live;
        }

        return Array.Empty<Il2CppObjectBase>();
    }

    // Types whose registered Resources folder this process has loaded. A folder load
    // walks every entry beneath it (3891 for Data/Conversations) and costs a good part
    // of a tenth of a second, so it runs once, and again only after a lookup misses:
    // the game's own UnloadUnusedAssets may drop a conversation nothing references,
    // and a later pass then needs it back.
    private static readonly HashSet<Type> ResourcesFoldersLoaded = new();

    /// <summary>
    /// Loads the Resources folder registered for a non-DataTemplate type so that
    /// <c>Resources.FindObjectsOfTypeAll</c> sees its assets. The clone pass runs at
    /// the title screen before the game has loaded any conversation, so without this
    /// every ConversationTemplate source reads as missing until some later load pulls
    /// it in. Returns true when this call loaded the folder, which tells a caller that
    /// missed that a second scan can see more than the first did.
    /// </summary>
    private static bool EnsureResourcesFolderLoaded(string templateTypeName, Type resolvedType, bool reload = false)
    {
        var folder = NonDataTemplateIdentityRegistry.GetResourcesFolder(templateTypeName, resolvedType);
        if (folder == null)
            return false;
        if (!reload && ResourcesFoldersLoaded.Contains(resolvedType))
            return false;

        try
        {
            using var timing = StartupTimings.Measure("resource folder", folder);
            Resources.LoadAll(folder, Il2CppType.From(resolvedType));
        }
        catch
        {
            return false;
        }

        ResourcesFoldersLoaded.Add(resolvedType);
        return true;
    }

    /// <summary>
    /// Forces <c>DataTemplateLoader</c> to materialise the per-type cache for
    /// <paramref name="templateType"/> if it is a <c>DataTemplate</c> subtype,
    /// by invoking <c>GetAll&lt;T&gt;()</c> reflectively and discarding the
    /// result. Used by clone application to ensure ancestor
    /// <c>m_TemplateMaps</c>/<c>m_TemplateArrays</c> slots exist before we
    /// mirror clones into them, so MENACE's lazy-snapshot consumers (e.g.
    /// <c>OwnedItems.Init</c>) see clones in their first enumeration.
    /// No-op for non-<c>DataTemplate</c> types.
    /// </summary>
    public static void EnsureDataTemplateSlotMaterialised(Type templateType)
    {
        if (templateType == null) return;
        if (!typeof(DataTemplate).IsAssignableFrom(templateType)) return;
        TryInvokeGetAll(templateType);
    }

    private static IReadOnlyList<Il2CppObjectBase> EnumerateScriptableObjects(Type resolvedType)
    {
        var il2CppType = Il2CppType.From(resolvedType);
        var candidates = Resources.FindObjectsOfTypeAll(il2CppType);
        if (candidates == null || candidates.Length == 0)
            return Array.Empty<Il2CppObjectBase>();

        var tryCast = Il2CppReflectiveCast.GetTryCastMethod(resolvedType);
        var results = new List<Il2CppObjectBase>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (candidate == null)
                continue;
            if (tryCast.Invoke(candidate, null) is Il2CppObjectBase cast)
                results.Add(cast);
        }

        return results;
    }

    /// <summary>
    /// Resolves a template type name to a concrete non-abstract Type. Searches
    /// the DataTemplateLoader's assembly first (the common case: game
    /// templates live there), then falls back to all loaded Il2Cpp wrapper
    /// assemblies. The fallback exists because some asset types (e.g.
    /// <c>Stem.SoundBank</c>) live in <c>Assembly-CSharp-firstpass</c> rather
    /// than the main <c>Assembly-CSharp</c>. Supports both short and
    /// fully-qualified names. Ambiguous short names produce a clear error
    /// listing candidates.
    /// </summary>
    public static Type ResolveTemplateType(string templateTypeName, out string error)
    {
        error = null;

        // A ns:Name names a code-defined [JiangyuType] injected at mod load.
        // Checked ahead of the cache so a later-injected type always wins over
        // a name that happened to resolve against a game assembly earlier.
        if (JiangyuTypeRegistry.TryResolve(templateTypeName, out var injected))
            return injected;

        EnsureAssemblyLoadHook();
        if (TryGetCachedType(templateTypeName, out var memoised))
            return memoised;

        var primaryMatch = ResolveInAssembly(typeof(DataTemplateLoader).Assembly, templateTypeName, out var primaryAmbiguous, out var primaryCandidates);
        if (primaryMatch != null)
            return CacheResolvedType(templateTypeName, primaryMatch);
        if (primaryAmbiguous)
        {
            error = $"template type name '{templateTypeName}' is ambiguous; candidates: {string.Join(", ", primaryCandidates)}.";
            return null;
        }

        // Fallback: walk all loaded assemblies. Filtering by an `Il2Cpp` name
        // prefix is wrong because Il2CppInterop-generated wrapper assemblies
        // keep their original filenames (e.g. `Assembly-CSharp-firstpass`
        // hosts the `Il2CppStem` namespace). The type-name match below
        // self-filters: only assemblies that actually expose a matching type
        // contribute, so cost stays bounded even when walking every loaded
        // assembly.
        var fallbackMatches = new List<Type>();
        var primaryAssembly = typeof(DataTemplateLoader).Assembly;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == primaryAssembly)
                continue;
            if (asm.IsDynamic)
                continue;
            var match = ResolveInAssembly(asm, templateTypeName, out var ambiguousInAsm, out var asmCandidates);
            if (match != null)
                fallbackMatches.Add(match);
            else if (ambiguousInAsm && asmCandidates != null)
                fallbackMatches.AddRange(asmCandidates);
        }

        if (fallbackMatches.Count == 1)
            return CacheResolvedType(templateTypeName, fallbackMatches[0]);
        if (fallbackMatches.Count > 1)
        {
            var candidates = string.Join(", ", fallbackMatches.Select(t => t.FullName).Distinct());
            error = $"template type name '{templateTypeName}' is ambiguous across loaded assemblies; candidates: {candidates}.";
            return null;
        }

        error = $"no template type '{templateTypeName}' found in any loaded assembly.";
        return null;
    }

    private static Type ResolveInAssembly(Assembly assembly, string templateTypeName, out bool ambiguous, out List<Type> candidates)
    {
        ambiguous = false;
        candidates = null;

        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t != null).ToArray();
        }
        catch
        {
            return null;
        }

        var exact = allTypes.FirstOrDefault(t => t.FullName == templateTypeName && !t.IsAbstract);
        if (exact != null)
            return exact;

        var matches = allTypes.Where(t => t.Name == templateTypeName && !t.IsAbstract).ToArray();
        if (matches.Length == 1)
            return matches[0];

        if (matches.Length > 1)
        {
            ambiguous = true;
            candidates = matches.ToList();
        }
        return null;
    }

    /// <summary>
    /// Reads the serialised m_ID identity of a live template via reflection,
    /// falling back to alternate wrapper-member shapes if m_ID isn't directly
    /// exposed. Returns null when no candidate yields a non-empty string.
    /// </summary>
    public static string ReadTemplateId(object template)
    {
        if (template == null)
            return null;

        var type = template.GetType();
        foreach (var candidate in IdMemberCandidates)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(
                    candidate,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    var result = TryReadAsString(() => property.GetValue(template));
                    if (!string.IsNullOrWhiteSpace(result))
                        return result;
                }

                var field = current.GetField(
                    candidate,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    var result = TryReadAsString(() => field.GetValue(template));
                    if (!string.IsNullOrWhiteSpace(result))
                        return result;
                }
            }
        }

        return null;
    }

    private static string TryReadAsString(Func<object> reader)
    {
        try
        {
            return reader()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // Cache the open GetAll<> definition and each closed instantiation because
    // clone and patch passes repeatedly probe the same template families.
    // The resolved flag is set last, so a caller that sees it set also sees
    // the definition it guards.
    private static MethodInfo _getAllDefinition;
    private static bool _getAllDefinitionResolved;
    private static readonly Dictionary<Type, MethodInfo> GetAllByType = new();

    private static object TryInvokeGetAll(Type templateType)
    {
        if (!_getAllDefinitionResolved)
        {
            _getAllDefinition = typeof(DataTemplateLoader)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "GetAll"
                                          && method.IsGenericMethodDefinition
                                          && method.GetParameters().Length == 0);
            _getAllDefinitionResolved = true;
        }

        if (_getAllDefinition == null)
            return null;

        if (!GetAllByType.TryGetValue(templateType, out var bound))
        {
            try
            {
                bound = _getAllDefinition.MakeGenericMethod(templateType);
            }
            catch
            {
                bound = null;
            }
            GetAllByType[templateType] = bound;
        }

        if (bound == null)
            return null;

        try
        {
            using var timing = StartupTimings.Measure("template cache", templateType.Name);
            return bound.Invoke(null, null);
        }
        catch
        {
            return null;
        }
    }

    private static List<Il2CppObjectBase> MaterialiseTemplates(object collection, Type templateType)
    {
        // Il2CppInterop's non-generic IEnumerable returns each item as a base
        // Il2CppObjectBase proxy, which doesn't expose the specific wrapper's
        // members via reflection. We need to TryCast each element to the
        // resolved wrapper type before the applier reads fields off it.
        var tryCastBound = Il2CppReflectiveCast.GetTryCastMethod(templateType, throwIfMissing: false);

        var results = new List<Il2CppObjectBase>();

        if (collection is Il2CppObjectBase il2CppCollection)
        {
            try
            {
                var enumerable = il2CppCollection.TryCast<Il2CppEnumerable>();
                if (enumerable != null)
                {
                    var enumerator = enumerable.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var current = enumerator.Current;
                        if (current == null)
                            continue;

                        var casted = TryCastToTarget(tryCastBound, current);
                        if (casted != null)
                            results.Add(casted);
                    }

                    if (results.Count > 0)
                        return results;
                }
            }
            catch
            {
            }
        }

        try
        {
            var getEnumeratorMethod = collection.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "GetEnumerator" && method.GetParameters().Length == 0);

            if (getEnumeratorMethod != null)
            {
                var enumerator = getEnumeratorMethod.Invoke(collection, null);
                if (enumerator != null)
                {
                    var enumeratorType = enumerator.GetType();
                    var moveNextMethod = enumeratorType.GetMethod(
                        "MoveNext", BindingFlags.Public | BindingFlags.Instance);
                    var currentProperty = enumeratorType.GetProperty(
                        "Current", BindingFlags.Public | BindingFlags.Instance);

                    if (moveNextMethod != null && currentProperty != null)
                    {
                        while ((bool)moveNextMethod.Invoke(enumerator, null))
                        {
                            var item = currentProperty.GetValue(enumerator);
                            if (item is not Il2CppObjectBase il2CppItem)
                                continue;

                            var casted = TryCastToTarget(tryCastBound, il2CppItem);
                            if (casted != null)
                                results.Add(casted);
                        }

                        if (results.Count > 0)
                            return results;
                    }
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static Il2CppObjectBase TryCastToTarget(MethodInfo tryCastBound, Il2CppObjectBase raw)
    {
        if (tryCastBound == null)
            return raw;

        try
        {
            return tryCastBound.Invoke(raw, null) as Il2CppObjectBase;
        }
        catch
        {
            return null;
        }
    }
}
