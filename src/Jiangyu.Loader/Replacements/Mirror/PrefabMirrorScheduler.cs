using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Owns the "bring an addition prefab up to parity with a vanilla
/// reference" queue. Two mirrors run off it: <see cref="HumanoidPrefabMirror"/>
/// for soldier-shape additions, and <see cref="SubassemblyScriptMirror"/>
/// for vanilla sub-assemblies copied into any prefab. Both read from a
/// vanilla prefab that MENACE's asset registry only holds once boot is
/// past mod load.
///
/// <para><see cref="Queue"/> is called at bundle-register time and tries
/// both immediately. A prefab whose reference is not loaded yet stays in
/// the queue and <see cref="DrainPending"/> retries it during the loader's
/// ApplyReplacements pass. Lives next to the appliers because it's an
/// apply-shaped concern (sequencing of patches against live state), not a
/// catalogue concern.</para>
/// </summary>
internal sealed class PrefabMirrorScheduler
{
    private sealed class Pending
    {
        public GameObject Prefab;
        public string Label;
        public bool Warned;
    }

    private readonly List<Pending> _pending = new();

    /// <summary>
    /// Try to mirror <paramref name="prefab"/> immediately, queueing it for
    /// the next drain if a reference isn't available yet. Returns a short
    /// suffix the caller can append to its register-line log.
    /// <paramref name="label"/> is the registered bundle key, used to name
    /// the prefab in log lines (its Object.name is `main` for every
    /// conventionally laid out bundle).
    /// </summary>
    public string Queue(GameObject prefab, string label, MelonLogger.Instance log)
    {
        var notes = new List<string>();
        var pending = false;

        if (HumanoidPrefabMirror.HasReferenceSentinel(prefab))
        {
            if (HumanoidPrefabMirror.Mirror(prefab, log))
            {
                notes.Add("component-mirrored");
            }
            else
            {
                pending = true;
                notes.Add("queued for component mirror");
            }
        }

        if (SubassemblyScriptMirror.HasSentinel(prefab))
        {
            if (SubassemblyScriptMirror.Mirror(prefab, label, log))
            {
                notes.Add("sub-assembly scripts restored");
            }
            else
            {
                pending = true;
                notes.Add("queued for sub-assembly script restore");
            }
        }

        if (pending)
            _pending.Add(new Pending { Prefab = prefab, Label = label });

        return notes.Count == 0 ? string.Empty : "; " + string.Join("; ", notes);
    }

    /// <summary>
    /// Drain the queue: retry every prefab whose reference was missing on
    /// an earlier pass. Both mirrors are idempotent (each renames its own
    /// sentinel once it's done with it and reports success when there's
    /// nothing left to do), so a prefab that half-succeeded picks up
    /// exactly the outstanding half. A prefab still unresolved after its
    /// first drain (the registry is populated by then) gets its missing
    /// reference surfaced once, so a misspelt sentinel is diagnosable.
    /// </summary>
    /// <summary>Whether a prefab still waits for a vanilla reference the registry did not hold when it loaded.</summary>
    public bool HasPending => _pending.Count > 0;

    public void DrainPending(MelonLogger.Instance log)
    {
        if (_pending.Count == 0) return;

        var mirrored = 0;
        _pending.RemoveAll(entry =>
        {
            if (entry.Prefab == null) return true;
            var warnOnce = !entry.Warned;
            var humanoid = HumanoidPrefabMirror.Mirror(
                entry.Prefab, log, warnWhenReferenceMissing: warnOnce);
            var subassemblies = SubassemblyScriptMirror.Mirror(
                entry.Prefab, entry.Label, log, warnWhenReferenceMissing: warnOnce);
            if (!humanoid || !subassemblies)
            {
                entry.Warned = true;
                return false;
            }
            mirrored++;
            return true;
        });

        if (mirrored > 0)
            log.Msg($"Prefab mirror: {mirrored} addition prefab(s) configured from vanilla reference.");
    }
}
