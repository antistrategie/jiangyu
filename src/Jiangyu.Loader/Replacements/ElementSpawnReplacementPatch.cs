using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Jiangyu.Loader.Runtime;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Applies a spawned unit's mesh or prefab replacement when its model is built, by
/// Harmony-postfixing <c>Element.OnSpawned</c>. At that point the element's renderers are
/// fully populated in its own <c>GetRenderers()</c> list (they are not yet parented under
/// <c>Element.m_Mesh</c>, so a transform scan finds nothing), so the SkinnedMeshRenderers
/// are pulled from that list and handed to the coordinator. Replaces the steady-state
/// spawn-monitor poll: a late spawn (player deployment, reinforcement) gets its swap at
/// spawn time. The coordinator owns the apply logic and is handed in via the static
/// <see cref="Coordinator"/> because the postfix is static.
/// </summary>
internal sealed class ElementSpawnReplacementPatch : IHarmonyPatchModule
{
    internal static ReplacementCoordinator Coordinator;
    private static MelonLogger.Instance _log;

    private readonly ReplacementCoordinator _coordinator;

    public ElementSpawnReplacementPatch(ReplacementCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        Coordinator = _coordinator;
        _log = context.Log;
        HarmonyPatching.TryPostfix(harmony, "Il2CppMenace.Tactical.Element", "OnSpawned",
            typeof(ElementSpawnReplacementPatch), nameof(OnSpawnedPostfix), _log, "spawn replacement");
    }

    private static void OnSpawnedPostfix(Element __instance)
    {
        try
        {
            var coordinator = Coordinator;
            if (coordinator == null || __instance == null)
                return;

            var groups = __instance.GetRenderers();
            if (groups == null)
                return;

            // Each SharedMaterialsRenderers groups one or more renderers sharing materials.
            // Collect the skinned ones (the mesh-replacement target surface).
            var renderers = new List<SkinnedMeshRenderer>();
            foreach (var group in groups)
            {
                var grouped = group?.GetRenderers();
                if (grouped == null)
                    continue;

                foreach (var renderer in grouped)
                {
                    var smr = renderer == null ? null : renderer.TryCast<SkinnedMeshRenderer>();
                    if (smr != null)
                        renderers.Add(smr);
                }
            }

            coordinator.ApplyToSpawnedRenderers(_log, renderers);
        }
        catch (Exception ex)
        {
            _log?.Error($"spawn replacement: Element.OnSpawned postfix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
