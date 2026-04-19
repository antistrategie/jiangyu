using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
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
    /// Returns all live templates of the given type (passed as either a simple
    /// name like "EntityTemplate" or a full name like
    /// "Il2CppMenace.Tactical.EntityTemplate"), or an empty list if the
    /// collection has not yet been populated. Callers retry on empty.
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

        var collection = TryInvokeGetAll(type);
        return collection == null
            ? Array.Empty<Il2CppObjectBase>()
            : MaterialiseTemplates(collection, type);
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
