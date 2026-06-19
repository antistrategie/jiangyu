using System.Reflection;
using MelonLoader;

namespace Jiangyu.Loader.Runtime.Patching;

/// <summary>
/// Resolves a MENACE (Il2Cpp) method by class name and parameter-type-name suffixes, scanning every
/// loaded assembly. Used by the loader's Harmony patch modules to bind a method by name across game
/// versions, including overload disambiguation by parameter types, which the simpler
/// <see cref="HarmonyPatching"/> (single-overload, by exact name) cannot do.
/// </summary>
internal static class Il2CppMethodResolver
{
    private const BindingFlags MethodFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Find a method whose declaring type's (generic-arity-stripped) name matches <paramref name="className"/>
    /// (exact when <paramref name="exact"/>, else by suffix) and whose parameter type names each contain the
    /// corresponding entry in <paramref name="paramTypeSuffixes"/>. Logs under <paramref name="label"/> when
    /// more than one type matches. Returns null when nothing matches.
    /// </summary>
    public static MethodInfo Find(string className, string methodName, string[] paramTypeSuffixes,
        bool exact, MelonLogger.Instance log, string label)
    {
        MethodInfo best = null;
        string bestTypeName = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var type in types)
            {
                if (type?.FullName == null || !type.FullName.StartsWith("Il2Cpp", StringComparison.Ordinal)) continue;
                var name = StripGenericArity(type.Name);
                if (exact ? name != className : !name.EndsWith(className, StringComparison.Ordinal)) continue;

                MethodInfo[] methods;
                try { methods = type.GetMethods(MethodFlags); } catch { continue; }
                foreach (var m in methods)
                {
                    if (m.Name != methodName) continue;
                    var p = m.GetParameters();
                    if (p.Length != paramTypeSuffixes.Length) continue;
                    bool match = true;
                    for (int i = 0; i < p.Length; i++)
                        if (!p[i].ParameterType.Name.Contains(paramTypeSuffixes[i])) { match = false; break; }
                    if (!match) continue;

                    if (best == null)
                    {
                        best = m;
                        bestTypeName = type.FullName;
                    }
                    else if (type.FullName != bestTypeName)
                    {
                        log?.Warning($"{label}: multiple '{className}' candidates; keeping {bestTypeName}, ignoring {type.FullName}.");
                    }
                }
            }
        }
        return best;
    }

    /// <summary>The type name without its generic arity marker (e.g. <c>List`1</c> becomes <c>List</c>).</summary>
    public static string StripGenericArity(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var backtick = name.IndexOf('`');
        return backtick < 0 ? name : name[..backtick];
    }
}
