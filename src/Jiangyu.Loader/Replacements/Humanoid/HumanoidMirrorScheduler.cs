using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Owns the "mirror humanoid addition prefab from reference soldier"
/// queue. <see cref="Queue"/> is called at bundle-register time and tries
/// to mirror immediately; if the reference isn't loaded yet, the prefab
/// stays in the queue and <see cref="DrainPending"/> retries during the
/// loader's ApplyReplacements pass once <c>Resources</c> can see the
/// vanilla soldier. Lives next to the appliers because it's an
/// apply-shaped concern (sequencing of patches against live state), not
/// a catalogue concern.
/// </summary>
internal sealed class HumanoidMirrorScheduler
{
    private readonly List<GameObject> _pending = new();

    /// <summary>
    /// Try to mirror <paramref name="prefab"/> immediately; queue it for
    /// the next drain if the reference isn't available yet. Returns a
    /// short suffix the caller can append to its register-line log.
    /// </summary>
    public string Queue(GameObject prefab, MelonLogger.Instance log)
    {
        if (!HumanoidPrefabMirror.HasReferenceSentinel(prefab))
            return string.Empty;
        if (HumanoidPrefabMirror.Mirror(prefab, log))
            return "; component-mirrored";
        _pending.Add(prefab);
        return "; queued for component mirror";
    }

    /// <summary>
    /// Drain the queue: retry every prefab that previously failed and
    /// patch any live instances that beat the mirror to the punch. The
    /// instance scan covers <c>main(Clone)</c>-style objects that MENACE
    /// pre-instantiated before the reference lookup could succeed (leader
    /// portrait warmup etc.); they inherit the un-mirrored sentinel from
    /// their prefab and the mirror picks them up by walking the
    /// GameObject registry.
    /// </summary>
    public void DrainPending(MelonLogger.Instance log)
    {
        if (_pending.Count == 0) return;

        var mirrored = 0;
        _pending.RemoveAll(prefab =>
        {
            if (prefab == null) return true;
            if (!HumanoidPrefabMirror.Mirror(prefab, log)) return false;
            mirrored++;
            return true;
        });

        if (mirrored > 0)
            log.Msg($"Humanoid mirror: {mirrored} addition prefab(s) configured from vanilla reference.");
    }
}
