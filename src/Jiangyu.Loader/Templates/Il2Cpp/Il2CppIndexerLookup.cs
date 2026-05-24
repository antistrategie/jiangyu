using System.Reflection;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Managed-reflection lookups for Il2CppInterop wrapper indexers. The
/// wrappers shape their <c>List&lt;T&gt;</c>-style collections as a
/// non-public <c>this[int]</c> property plus a public one, often inherited
/// from a wrapper base. Consolidating the lookup avoids re-implementing
/// the binding-flag dance per call site.
/// </summary>
internal static class Il2CppIndexerLookup
{
    /// <summary>
    /// First single-parameter int-keyed indexer on <paramref name="type"/>
    /// (or one of its bases) that supports reads. Used by collection
    /// access paths in the patch applier and the conversation manager
    /// registry.
    /// </summary>
    public static PropertyInfo FindIntIndexer(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var property in current.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var parameters = property.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int) && property.CanRead)
                    return property;
            }
        }
        return null;
    }

    /// <summary>
    /// First single-parameter indexer keyed by <paramref name="keyType"/>.
    /// Used by the clone applier when iterating a typed-state dictionary
    /// keyed by an IL2CPP type wrapper.
    /// </summary>
    public static PropertyInfo FindIndexerByKeyType(Type type, Type keyType)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == keyType)
                return property;
        }
        return null;
    }
}
