using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Jiangyu.Loader.Runtime;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;
using UnityEngine;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;

namespace Jiangyu.Loader.Templates;

// JIANGYU-CONTRACT: DataTemplateLoader.LoadTemplates<T> calls the non-generic
// Resources.LoadAll(string, Type), then builds both lookup tables from its result.
// Appending here preserves the first GetAll<Ancestor>() snapshot without forcing
// a broad ancestor folder such as EffectListTemplate's Data/ to load during cloning.
internal sealed class TemplateCloneAncestorPatch : IHarmonyPatchModule
{
    private static readonly TemplateAncestorRegistry<DataTemplate> Registry = new();
    private static readonly Dictionary<Type, (IntPtr NativeType, string Folder)> Types = new();
    private static MelonLogger.Instance _log;
    private readonly TemplateCloneApplier _clones;

    public static bool Installed { get; private set; }

    public TemplateCloneAncestorPatch(TemplateCloneApplier clones) => _clones = clones;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        if (!_clones.HasConfiguredClones)
            return;
        _log = context.Log;
        var target = AccessTools.Method(typeof(Resources), nameof(Resources.LoadAll),
            new[] { typeof(string), typeof(Il2CppSystem.Type) });
        try
        {
            if (target == null)
                throw new MissingMethodException("Resources.LoadAll(string, Type)");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(TemplateCloneAncestorPatch), nameof(Postfix)));
            Installed = true;
            HarmonyPatching.Installed(_log, "Patched Resources.LoadAll for deferred template ancestor registration.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Deferred template ancestor registration unavailable, using eager registration: {ex.Message}");
        }
    }

    public static void Remember(Type type, string id, DataTemplate clone)
    {
        if (!Types.TryGetValue(type, out var slot))
        {
            var nativeType = Il2CppType.From(type);
            slot = (nativeType.Pointer, DataTemplateLoader.GetBaseFolder(nativeType));
            Types[type] = slot;
        }
        Registry.Remember(slot.NativeType, slot.Folder, id, clone);
    }

    private static void Postfix(string __0, Il2CppSystem.Type __1,
        ref Il2CppReferenceArray<UnityEngine.Object> __result)
    {
        if (__1 == null || __result == null || !Registry.TryGet(__1.Pointer, __0, out var clones))
            return;

        using var timing = StartupTimings.Measure("deferred ancestor registration", __0);
        var missing = TemplateAncestorRegistry<DataTemplate>.Missing(clones, LoadedIds(__result));
        if (missing.Count == 0)
            return;
        var merged = new Il2CppReferenceArray<UnityEngine.Object>(__result.Length + missing.Count);
        for (var i = 0; i < __result.Length; i++)
            merged[i] = __result[i];
        for (var i = 0; i < missing.Count; i++)
            merged[__result.Length + i] = missing[i];
        __result = merged;
    }

    private static IEnumerable<string> LoadedIds(Il2CppReferenceArray<UnityEngine.Object> loaded)
    {
        foreach (var asset in loaded)
        {
            var template = asset?.TryCast<DataTemplate>();
            if (template != null)
                yield return template.GetID();
        }
    }
}
