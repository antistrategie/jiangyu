using System.Collections;
using Il2CppInterop.Runtime;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tools;
using Jiangyu.Sdk;
using UnityEngine;

namespace JiangyuLateHarness;

// Registers templates into the game's own template maps on a fixed clock, outside the
// loader's clone pass: what another MelonLoader mod does, on its own schedule. The KDL
// beside this file depends on these ids, so the loader has to hold the dependent work and
// land it when each id appears. The first id lands inside the scene's poll schedule, the
// second after it, on the steady-state pass.
public sealed class LateRegistrarSystem : JiangyuSystem
{
    private const string SourceId = "enemy.pirate_scavengers";

    private static readonly (string Id, float AfterSeconds)[] Schedule =
    {
        ("harness.late_entity", 4f),
        ("harness.late_entity_2", 25f),
    };

    private bool _started;

    // Whichever comes first: the title scene (the loader's first pass for it runs at the
    // same scene-load moment, before the delay elapses) or the templates-applied signal.
    public override void OnSceneLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != null && sceneName.Contains("Title"))
            Start();
    }

    public override void OnTemplatesApplied() => Start();

    private void Start()
    {
        if (_started)
            return;
        _started = true;
        Log.Info("late registrar: clock started");
        Context.Coroutines.Start(Run());
    }

    private IEnumerator Run()
    {
        var start = Time.realtimeSinceStartup;
        foreach (var (id, after) in Schedule)
        {
            while (Time.realtimeSinceStartup - start < after)
                yield return null;
            Register(id, Time.realtimeSinceStartup - start);
        }
    }

    // A copy of a vanilla entity under a new id, written into the concrete map and every
    // ancestor map the game has materialised, the way another loader registers a clone.
    private static void Register(string id, float at)
    {
        try
        {
            if (!DataTemplateLoader.TryGet<EntityTemplate>(SourceId, out var source) || source == null)
            {
                Log.Warn($"late registrar: source {SourceId} not found; {id} not registered");
                return;
            }

            var clone = UnityEngine.Object.Instantiate(source.Cast<UnityEngine.Object>());
            clone.name = id;
            clone.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var template = clone.Cast<DataTemplate>();
            template.m_ID = id;

            var maps = DataTemplateLoader.GetSingleton().m_TemplateMaps;
            var slots = 0;
            for (var type = typeof(EntityTemplate);
                 type != null && type != typeof(DataTemplate) && typeof(DataTemplate).IsAssignableFrom(type);
                 type = type.BaseType)
            {
                if (maps.TryGetValue(Il2CppType.From(type), out var map) && map != null)
                {
                    map[id] = template;
                    slots++;
                }
            }

            Log.Info($"late registrar: registered EntityTemplate:{id} in {slots} map slot(s) at {at:F1}s");
        }
        catch (Exception ex)
        {
            Log.Error($"late registrar: {id}: {ex}");
        }
    }
}
