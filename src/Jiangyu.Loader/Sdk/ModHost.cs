using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Sdk.Hooks;
using Jiangyu.Loader.Sdk.Patches;
using Jiangyu.Loader.Sdk.State;
using Jiangyu.Loader.Sdk.Types;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk;

/// <summary>A registered mod system with its isolation bookkeeping.</summary>
internal sealed class LoadedSystem
{
    public LoadedSystem(string modId, string typeName, JiangyuSystem instance)
    {
        ModId = modId;
        TypeName = typeName;
        Instance = instance;
    }

    public string ModId { get; }
    public string TypeName { get; }
    public JiangyuSystem Instance { get; }
    public bool Quarantined { get; internal set; }
    public int Failures { get; internal set; }
}

/// <summary>
/// Owns the loaded mod systems: discovery, dependency ordering, instantiation, context
/// binding, lifecycle forwarding, and per-mod error isolation. A system whose lifecycle
/// call throws is logged, and after repeated failures it is quarantined so it cannot
/// keep destabilising the session or starve the other systems. One bad system never
/// breaks another.
/// </summary>
internal sealed class ModHost
{
    private const int QuarantineThreshold = 3;

    private readonly IModHostLog _log;
    private readonly Func<string, ModContext> _contextFactory;
    private readonly Dictionary<string, ModContext> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<Assembly, ModContext> _contextsByAssembly = new();
    private readonly List<LoadedSystem> _systems = new();

    public ModHost(IModHostLog log, Func<string, ModContext> contextFactory)
    {
        _log = log;
        _contextFactory = contextFactory;
    }

    public IReadOnlyList<LoadedSystem> Systems => _systems;

    /// <summary>The bound contexts, one per mod id. Used by the state store to
    /// persist each mod's <see cref="ModContext.State"/> on save/load.</summary>
    public IEnumerable<ModContext> Contexts => _contexts.Values;

    /// <summary>Discover, order, and register every concrete JiangyuSystem in an assembly.</summary>
    public IReadOnlyList<LoadedSystem> Register(Assembly modAssembly, string modId)
        => Register(new[] { modAssembly }, modId);

    /// <summary>
    /// Discover, order, and register every concrete JiangyuSystem across all of a mod's
    /// code assemblies. Ordering spans the whole mod, so a [DependsOn] resolves even when
    /// the dependency lives in a different DLL of the same mod.
    /// </summary>
    public IReadOnlyList<LoadedSystem> Register(IReadOnlyList<Assembly> modAssemblies, string modId)
    {
        // Two mods bundling an identically-identified DLL share one Assembly instance
        // (Assembly.LoadFrom returns the already-loaded one), so keep the first binding
        // and warn rather than silently rebinding its injected handlers and Log tag.
        foreach (var modAssembly in modAssemblies)
        {
            if (_contextsByAssembly.TryGetValue(modAssembly, out var existing))
            {
                if (!string.Equals(existing.ModId, modId, StringComparison.Ordinal))
                    _log.Warn(
                        $"[{modId}] shares assembly '{modAssembly.GetName().Name}' with mod '{existing.ModId}'; "
                        + $"its injected [JiangyuType] handlers and static Log lines resolve to '{existing.ModId}'.");
            }
            else
            {
                _contextsByAssembly[modAssembly] = ContextFor(modId);
            }
        }

        var registered = new List<LoadedSystem>();
        foreach (var type in OrderByDependencies(modAssemblies.SelectMany(ConcreteSystems).ToList(), modId))
        {
            try
            {
                var instance = (JiangyuSystem)Activator.CreateInstance(type)!;
                registered.Add(Adopt(modId, instance, type.FullName ?? type.Name));
                _log.Info($"[{modId}] registered system {type.Name}");
            }
            catch (Exception ex)
            {
                _log.Error($"[{modId}] failed to instantiate {type.FullName}: {ex.Message}");
            }
        }

        return registered;
    }

    /// <summary>Bind a context to an already-constructed system and track it.</summary>
    public LoadedSystem Adopt(string modId, JiangyuSystem instance)
        => Adopt(modId, instance, instance.GetType().FullName ?? instance.GetType().Name);

    /// <summary>Bind a context to an already-constructed system and track it under a known type name.</summary>
    public LoadedSystem Adopt(string modId, JiangyuSystem instance, string typeName)
    {
        instance.Context = ContextFor(modId);
        var system = new LoadedSystem(modId, typeName, instance);
        _systems.Add(system);
        return system;
    }

    /// <summary>Get-or-create the shared context for a mod id.</summary>
    public ModContext ContextFor(string modId)
    {
        if (!_contexts.TryGetValue(modId, out var context))
        {
            context = _contextFactory(modId);
            _contexts[modId] = context;
        }

        return context;
    }

    /// <summary>
    /// The context owning <paramref name="instance"/>, resolved by the mod assembly
    /// that defines its type, or null for an object outside any registered mod. Bound
    /// to <see cref="ModContext.For"/> so injected handlers can reach their mod.
    /// </summary>
    public ModContext ResolveContext(object instance)
        => instance != null && _contextsByAssembly.TryGetValue(instance.GetType().Assembly, out var context)
            ? context
            : null;

    /// <summary>The mod id owning an assembly, or null for an assembly outside any
    /// registered mod. Bound to <see cref="Jiangyu.Sdk.Log"/> so static log lines are
    /// tagged with the mod that emitted them.</summary>
    public string ModIdForAssembly(Assembly assembly)
        => assembly != null && _contextsByAssembly.TryGetValue(assembly, out var context)
            ? context.ModId
            : null;

    public void InitAll() => ForEachLive("OnInit", s => s.Instance.OnInit());

    public void TemplatesApplied() => ForEachLive("OnTemplatesApplied", s => s.Instance.OnTemplatesApplied());

    public void SceneLoaded(int buildIndex, string sceneName)
        => ForEachLive("OnSceneLoaded", s => s.Instance.OnSceneLoaded(buildIndex, sceneName));

    public void Update() => ForEachLive("OnUpdate", s => s.Instance.OnUpdate());

    public void UnloadAll()
    {
        // Tear down in reverse dependency order (LIFO): each system unloads before the
        // systems it was ordered after, mirroring the dependency-first init order.
        ForEachLive("OnUnload", s => s.Instance.OnUnload(), reverse: true);
        foreach (var system in _systems)
            CleanupSystem(system);
    }

    private void ForEachLive(string phase, Action<LoadedSystem> body, bool reverse = false)
    {
        foreach (var system in reverse ? Enumerable.Reverse(_systems) : _systems)
        {
            if (system.Quarantined)
                continue;

            try
            {
                body(system);
            }
            catch (Exception ex)
            {
                system.Failures++;
                _log.Error($"[{system.ModId}] {phase} threw ({system.Failures}/{QuarantineThreshold}): {ex}");
                if (system.Failures >= QuarantineThreshold)
                {
                    system.Quarantined = true;
                    // Coroutines and patches are mod-scoped (shared by every system of
                    // this modId), so release them only once no sibling system is still
                    // live; quarantining one system must not stop another's work.
                    if (!_systems.Any(other => other != system && other.ModId == system.ModId && !other.Quarantined))
                        CleanupSystem(system);
                    _log.Warn($"[{system.ModId}] quarantined after {system.Failures} failures. Its lifecycle calls are now skipped.");
                }
            }
        }
    }

    // Release a mod's runtime attachments: stop its coroutines and drop its method
    // patches, so a quarantined or unloaded system leaves nothing running.
    private static void CleanupSystem(LoadedSystem system)
    {
        var context = system.Instance.Context;
        (context?.Coroutines as ModCoroutineRunner)?.StopAll();
        (context?.Patches as ModPatchService)?.RemoveAll();
    }

    private static IEnumerable<Type> ConcreteSystems(Assembly assembly)
        => ModAssemblyScan.SafeGetTypes(assembly).Where(t =>
            typeof(JiangyuSystem).IsAssignableFrom(t)
            && t.IsClass
            && !t.IsAbstract
            && t.GetConstructor(Type.EmptyTypes) != null);

    // Order a mod's systems so each runs after the systems it depends on. The base
    // order is the systems' full type name, deterministic regardless of reflection
    // order. A [DependsOn] edge then pulls a dependency ahead of its dependent. A
    // listed dependency that is not a system of this mod (or the system itself, or
    // null) is ignored with a warning, and a cycle is broken by leaving its members
    // in name order. Systems register in this order, so the init, scene, and update
    // phases dispatch in dependency order. UnloadAll iterates it in reverse for LIFO
    // teardown.
    internal IReadOnlyList<Type> OrderByDependencies(IReadOnlyList<Type> systems, string modId)
    {
        var byName = systems.Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        var present = new HashSet<Type>(byName);
        var deps = new Dictionary<Type, List<Type>>();
        foreach (var type in byName)
        {
            var effective = new List<Type>();
            foreach (var dep in type.GetCustomAttribute<DependsOnAttribute>()?.Systems ?? Type.EmptyTypes)
            {
                if (dep == null)
                    _log.Warn($"[{modId}] system {type.Name} has a null [DependsOn] entry; ignoring.");
                else if (dep == type)
                    _log.Warn($"[{modId}] system {type.Name} depends on itself; ignoring.");
                else if (!present.Contains(dep))
                    _log.Warn($"[{modId}] system {type.Name} depends on {dep.Name}, which is not a system of this mod; ignoring.");
                else
                    effective.Add(dep);
            }
            deps[type] = effective;
        }

        // Repeated sweeps in name order: place a system once all its dependencies are
        // placed. A dependency always lands before its dependents, and independent
        // systems keep their name order.
        var ordered = new List<Type>(byName.Count);
        var placed = new HashSet<Type>();
        bool progressed = true;
        while (ordered.Count < byName.Count && progressed)
        {
            progressed = false;
            foreach (var type in byName)
            {
                if (placed.Contains(type) || !deps[type].All(placed.Contains))
                    continue;
                ordered.Add(type);
                placed.Add(type);
                progressed = true;
            }
        }

        if (ordered.Count < byName.Count)
        {
            var cycle = byName.Where(t => !placed.Contains(t)).ToList();
            _log.Warn($"[{modId}] dependency cycle among systems: {string.Join(", ", cycle.Select(t => t.Name))}; running them in name order.");
            ordered.AddRange(cycle);
        }

        return ordered;
    }
}
