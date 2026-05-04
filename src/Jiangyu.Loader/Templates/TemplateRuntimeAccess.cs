using System.Reflection;
using Il2CppInterop.Runtime;
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
        template = null;
        error = null;
        var il2CppType = Il2CppType.From(resolvedType);
        var candidates = Resources.FindObjectsOfTypeAll(il2CppType);
        if (candidates == null || candidates.Length == 0)
            return false;

        // FindObjectsOfTypeAll returns UnityEngine.Object base wrappers; cast
        // to the specific resolved type so consumers storing into a typed
        // array (e.g. Il2CppReferenceArray<PerkTreeTemplate>) get a wrapper
        // of the correct element type.
        var tryCast = TryCastTemplate(resolvedType);
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

    // Resolves Il2CppObjectBase.TryCast<T>(). This method is part of the
    // Il2CppInterop runtime and always exists on Il2CppObjectBase; missing
    // means a fundamental runtime setup failure, so we throw rather than
    // silently return a less-specific wrapper.
    private static MethodInfo TryCastTemplate(Type resolvedType)
    {
        var method = typeof(Il2CppObjectBase)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            ?? throw new InvalidOperationException(
                "Il2CppObjectBase.TryCast<T>() not found — Il2CppInterop runtime missing or mismatched.");
        return method.MakeGenericMethod(resolvedType);
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
    public static IReadOnlyList<Il2CppObjectBase> GetAllTemplates(string templateTypeName, out Type resolvedType, out string resolveError)
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
            return EnumerateScriptableObjects(type);

        return Array.Empty<Il2CppObjectBase>();
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

        var tryCast = TryCastTemplate(resolvedType);
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
    /// Resolves a template type name to a concrete non-abstract Type from the
    /// DataTemplateLoader's assembly. Supports both short and fully-qualified
    /// names. Ambiguous short names produce a clear error listing candidates.
    /// </summary>
    public static Type ResolveTemplateType(string templateTypeName, out string error)
    {
        error = null;

        var assembly = typeof(DataTemplateLoader).Assembly;
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t != null).ToArray();
        }

        var exact = allTypes.FirstOrDefault(t => t.FullName == templateTypeName && !t.IsAbstract);
        if (exact != null)
            return exact;

        var matches = allTypes.Where(t => t.Name == templateTypeName && !t.IsAbstract).ToArray();
        if (matches.Length == 1)
            return matches[0];

        if (matches.Length > 1)
        {
            var candidates = string.Join(", ", matches.Select(t => t.FullName));
            error = $"template type name '{templateTypeName}' is ambiguous; candidates: {candidates}.";
            return null;
        }

        error = $"no template type '{templateTypeName}' found in the DataTemplateLoader's assembly.";
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

    private static object TryInvokeGetAll(Type templateType)
    {
        var loaderType = typeof(DataTemplateLoader);

        var getAllMethod = loaderType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "GetAll"
                                      && method.IsGenericMethodDefinition
                                      && method.GetParameters().Length == 0);

        if (getAllMethod == null)
            return null;

        try
        {
            return getAllMethod.MakeGenericMethod(templateType).Invoke(null, null);
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
        var tryCastBound = typeof(Il2CppObjectBase)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            ?.MakeGenericMethod(templateType);

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
