namespace Jiangyu.Codegen.Handlers;

/// <summary>
/// The type-graph walks the reflection shell needs when it turns a game type into a
/// <see cref="HandlerDoc"/>: which declaring types count as authoring surface, and what a
/// subtype is called on the page. Pure functions over <see cref="Type"/> so they are testable
/// with ordinary fixture classes.
/// </summary>
public static class HandlerTypeWalk
{
    // The roots above which nothing is authoring surface: Unity's own members
    // (name, hideFlags), Odin's serializationData, the interop pointer.
    private static readonly HashSet<string> RootFullNames = new(StringComparer.Ordinal)
    {
        "UnityEngine.Object",
        "UnityEngine.ScriptableObject",
        "UnityEngine.MonoBehaviour",
        "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase",
        "Il2CppSystem.Object",
        "System.Object",
    };

    private static readonly HashSet<string> RootNames = new(StringComparer.Ordinal)
    {
        "SerializedScriptableObject",
        "SerializedMonoBehaviour",
    };

    /// <summary>
    /// The full names of <paramref name="type"/> and its bases up to <paramref name="stopBase"/>
    /// (kept or dropped per <paramref name="inclusiveBase"/>). A stop base that never appears
    /// in the class chain, such as an interface, ends the walk at the first authoring root
    /// instead, so a subtype rooted in <c>SerializedScriptableObject</c> never lists Odin's or
    /// Unity's own members.
    /// </summary>
    public static HashSet<string> DeclaringTypeNames(Type type, Type stopBase, bool inclusiveBase)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = type; cur is not null; cur = cur.BaseType)
        {
            if (cur.FullName is null) break;
            var atBase = cur.FullName == stopBase.FullName;
            if (atBase && !inclusiveBase) break;
            if (IsAuthoringRoot(cur)) break;
            names.Add(cur.FullName);
            if (atBase) break;
        }
        return names;
    }

    /// <summary>
    /// The name a subtype renders under. Two subtypes of one section can share a short name,
    /// which the compiler then reports as ambiguous in <c>type=</c>; each renders under its full
    /// CLR name, the spelling <c>type=</c> resolves when the short name is not enough.
    /// </summary>
    public static string DisplayName(Type type, IReadOnlyList<Type> siblings)
        => siblings.Count(t => t.Name == type.Name) < 2 ? type.Name : type.FullName ?? type.Name;

    private static bool IsAuthoringRoot(Type type)
        => (type.FullName is { } fullName && RootFullNames.Contains(fullName))
            || RootNames.Contains(type.Name);
}
