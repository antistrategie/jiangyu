namespace Jiangyu.Shared.Templates;

/// <summary>
/// Cross-project reflection predicates for template-shape checks. Shared
/// between <c>Jiangyu.Core.Templates.TemplateTypeCatalog</c> (compile-time
/// schema introspection) and <c>Jiangyu.Loader.Templates.TemplatePatchApplier</c>
/// (runtime apply against live wrappers). Lives in Shared because Loader
/// targets net6.0 and cannot reference Core; previously each side
/// duplicated these checks against the same FullName strings.
/// </summary>
public static class TemplateTypeRules
{
    private const string BclHashSetFullName = "System.Collections.Generic.HashSet`1";
    private const string Il2CppSystemHashSetFullName = "Il2CppSystem.Collections.Generic.HashSet`1";

    /// <summary>
    /// True if <paramref name="type"/> is a <c>HashSet&lt;T&gt;</c> in either
    /// the BCL or the Il2CppSystem-wrapped namespace. Set semantics differ
    /// from <c>List&lt;T&gt;</c>: indexed Set/InsertAt are nonsensical and
    /// Remove takes a value rather than an index.
    /// </summary>
    public static bool IsHashSetCollection(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            var definitionName = current.GetGenericTypeDefinition().FullName;
            if (definitionName == Il2CppSystemHashSetFullName
                || definitionName == BclHashSetFullName)
                return true;
        }
        return false;
    }
}
