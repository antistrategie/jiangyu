using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk;

/// <summary>A registered code-mod entry point with its isolation bookkeeping.</summary>
internal sealed class LoadedMod
{
    public LoadedMod(string modId, string typeName, JiangyuMod instance)
    {
        ModId = modId;
        TypeName = typeName;
        Instance = instance;
    }

    public string ModId { get; }
    public string TypeName { get; }
    public JiangyuMod Instance { get; }
    public bool Quarantined { get; internal set; }
    public int Failures { get; internal set; }
}

/// <summary>
/// Owns the loaded code mods: discovery, instantiation, context binding,
/// lifecycle forwarding, and per-mod error isolation. A mod whose lifecycle call
/// throws is logged, and after repeated failures it is quarantined so it cannot
/// keep destabilising the session or starve the other mods. One bad mod never
/// breaks another.
/// </summary>
internal sealed class ModHost
{
    private const int QuarantineThreshold = 3;

    private readonly IModHostLog _log;
    private readonly Func<string, ModContext> _contextFactory;
    private readonly Dictionary<string, ModContext> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<Assembly, ModContext> _contextsByAssembly = new();
    private readonly List<LoadedMod> _mods = new();

    public ModHost(IModHostLog log, Func<string, ModContext> contextFactory)
    {
        _log = log;
        _contextFactory = contextFactory;
    }

    public IReadOnlyList<LoadedMod> Mods => _mods;

    /// <summary>The bound contexts, one per mod id. Used by the state store to
    /// persist each mod's <see cref="ModContext.State"/> on save/load.</summary>
    public IEnumerable<ModContext> Contexts => _contexts.Values;

    /// <summary>Discover and register every concrete JiangyuMod entry point in an assembly.</summary>
    public IReadOnlyList<LoadedMod> Register(Assembly modAssembly, string modId)
    {
        // Two mods bundling an identically-identified DLL share one Assembly instance
        // (Assembly.LoadFrom returns the already-loaded one), so keep the first binding
        // and warn rather than silently rebinding its injected handlers and Log tag.
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

        var registered = new List<LoadedMod>();
        foreach (var type in ConcreteJiangyuMods(modAssembly))
        {
            try
            {
                var instance = (JiangyuMod)Activator.CreateInstance(type)!;
                registered.Add(Adopt(modId, instance, type.FullName ?? type.Name));
                _log.Info($"[{modId}] registered mod entry {type.Name}");
            }
            catch (Exception ex)
            {
                _log.Error($"[{modId}] failed to instantiate {type.FullName}: {ex.Message}");
            }
        }

        return registered;
    }

    /// <summary>Bind a context to an already-constructed mod and track it.</summary>
    public LoadedMod Adopt(string modId, JiangyuMod instance)
        => Adopt(modId, instance, instance.GetType().FullName ?? instance.GetType().Name);

    /// <summary>Bind a context to an already-constructed mod and track it under a known type name.</summary>
    public LoadedMod Adopt(string modId, JiangyuMod instance, string typeName)
    {
        instance.Context = ContextFor(modId);
        var mod = new LoadedMod(modId, typeName, instance);
        _mods.Add(mod);
        return mod;
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

    public void InitAll() => ForEachLive("OnInit", m => m.Instance.OnInit());

    public void TemplatesApplied() => ForEachLive("OnTemplatesApplied", m => m.Instance.OnTemplatesApplied());

    public void SceneLoaded(int buildIndex, string sceneName)
        => ForEachLive("OnSceneLoaded", m => m.Instance.OnSceneLoaded(buildIndex, sceneName));

    public void Update() => ForEachLive("OnUpdate", m => m.Instance.OnUpdate());

    public void UnloadAll()
    {
        ForEachLive("OnUnload", m => m.Instance.OnUnload());
        foreach (var mod in _mods)
            CleanupMod(mod);
    }

    private void ForEachLive(string phase, Action<LoadedMod> body)
    {
        foreach (var mod in _mods)
        {
            if (mod.Quarantined)
                continue;

            try
            {
                body(mod);
            }
            catch (Exception ex)
            {
                mod.Failures++;
                _log.Error($"[{mod.ModId}] {phase} threw ({mod.Failures}/{QuarantineThreshold}): {ex.Message}");
                if (mod.Failures >= QuarantineThreshold)
                {
                    mod.Quarantined = true;
                    // Coroutines and patches are mod-scoped (shared by every entry point of
                    // this modId), so release them only once no sibling entry point is
                    // still live; quarantining one entry point must not stop another's work.
                    if (!_mods.Any(other => other != mod && other.ModId == mod.ModId && !other.Quarantined))
                        CleanupMod(mod);
                    _log.Warn($"[{mod.ModId}] quarantined after {mod.Failures} failures. Its lifecycle calls are now skipped.");
                }
            }
        }
    }

    // Release a mod's runtime attachments: stop its coroutines and drop its method
    // patches, so a quarantined or unloaded mod leaves nothing running.
    private static void CleanupMod(LoadedMod mod)
    {
        var context = mod.Instance.Context;
        (context?.Coroutines as ModCoroutineRunner)?.StopAll();
        (context?.Patches as ModPatchService)?.RemoveAll();
    }

    private static IEnumerable<Type> ConcreteJiangyuMods(Assembly assembly)
        => ModAssemblyScan.SafeGetTypes(assembly).Where(t =>
            typeof(JiangyuMod).IsAssignableFrom(t)
            && t.IsClass
            && !t.IsAbstract
            && t.GetConstructor(Type.EmptyTypes) != null);
}
