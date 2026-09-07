using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Full-name lookup of the managed types an <see cref="IAssemblyManager"/>
/// holds. The map is built once per manager and dies with it: the entry is
/// an ephemeron, so the map reaching back to the manager through
/// AsmResolver's module resolver does not keep the manager alive.
/// </summary>
internal static class AssemblyTypeResolver
{
    private static readonly ConditionalWeakTable<IAssemblyManager, Lazy<TypeMap>> Cache = new();

    /// <summary>
    /// Returns a resolver from a CLR full name (nested types joined with
    /// <c>+</c>, as <see cref="TypeDefinition.FullName"/> spells them) to the
    /// matching definition, or <c>null</c> when the manager holds no such type.
    /// </summary>
    public static Func<string, TypeDefinition?> For(IAssemblyManager assemblyManager)
    {
        ArgumentNullException.ThrowIfNull(assemblyManager);
        var index = Index(assemblyManager);
        return name => index.Value.Types.TryGetValue(name, out var type) ? type : null;
    }

    /// <summary>
    /// Builds the map now, on the calling thread, and reports its size. Call
    /// it before fanning inspection out across threads: the manager's
    /// assembly list is not safe to enumerate while other threads resolve
    /// scripts through the manager.
    /// </summary>
    public static TypeMapStats Warm(IAssemblyManager assemblyManager)
    {
        ArgumentNullException.ThrowIfNull(assemblyManager);
        return Index(assemblyManager).Value.Stats;
    }

    private static Lazy<TypeMap> Index(IAssemblyManager assemblyManager)
        => Cache.GetValue(
            assemblyManager,
            manager => new Lazy<TypeMap>(() => Build(manager), LazyThreadSafetyMode.ExecutionAndPublication));

    // One unreadable assembly loses only its own types; the enumeration of the
    // assembly list itself is not guarded, since a failure there means the
    // manager is unusable and the caller should hear about it.
    private static TypeMap Build(IAssemblyManager assemblyManager)
    {
        var map = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (AssemblyDefinition assembly in assemblyManager.GetAssemblies())
        {
            try
            {
                foreach (ModuleDefinition module in assembly.Modules)
                {
                    foreach (TypeDefinition type in EnumerateTypes(module.TopLevelTypes))
                        map.TryAdd(type.FullName, type);
                }
            }
            catch
            {
                skipped++;
            }
        }
        return new TypeMap(map, new TypeMapStats(map.Count, skipped));
    }

    private sealed record TypeMap(Dictionary<string, TypeDefinition> Types, TypeMapStats Stats);

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }
}

/// <summary>How much of the manager the type map covers.</summary>
/// <param name="Types">Distinct full names indexed.</param>
/// <param name="SkippedAssemblies">Assemblies whose types could not be read.</param>
internal sealed record TypeMapStats(int Types, int SkippedAssemblies);
