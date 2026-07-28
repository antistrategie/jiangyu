using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Restores the MENACE MonoBehaviours on a vanilla sub-assembly a modder
/// copied into their own prefab.
///
/// <para>Vanilla prefabs carry sub-assemblies that are more than geometry.
/// The CQB assault rifle's laser pointer, for instance, is three unlit
/// quads driven by <c>LookAtCamera</c>, <c>ExpandRetract</c> and
/// <c>VisibilityChangeListener</c>. Copying that sub-assembly into a mod
/// prefab in the Unity Editor brings the GameObjects, transforms, meshes
/// and materials across, but a modder's project has no reference to the
/// game's script assemblies, so every one of those components is lost on
/// the way into the AssetBundle and the laser renders as a dead quad.</para>
///
/// <para>The modder marks the copied sub-assembly with a child GameObject
/// named <c>__jiangyu_scripts:&lt;vanilla prefab&gt;</c>. At load time the
/// named vanilla prefab is looked up in Unity's asset registry, the node
/// the sentinel sits on is paired with its counterpart in that prefab, and
/// <see cref="Il2CppComponentMirror"/> walks the two subtrees restoring
/// components and their field state. The sentinel is renamed rather than
/// destroyed on completion so live clones inherit the done marker and a
/// later pass can tell restored sub-assemblies from pending ones.</para>
///
/// <para>The counterpart in the vanilla prefab is found by name: the
/// sentinel's parent node keeps whatever name it had in vanilla. Two forms
/// cover the rest. A sentinel directly on the addition's root pairs the two
/// prefab roots, which restores scripts across a whole imported prefab. An
/// explicit <c>@</c> suffix
/// (<c>__jiangyu_scripts:&lt;prefab&gt;@Path/To/Node</c>) names the
/// counterpart's path from the vanilla prefab root, for when the copied
/// node has been renamed or its name is not unique.</para>
/// </summary>
internal static class SubassemblyScriptMirror
{
    /// <summary>
    /// Name prefix on the sentinel child marking a copied vanilla
    /// sub-assembly. The remainder is the vanilla prefab's runtime
    /// Object.name, optionally followed by <c>@</c> and the counterpart's
    /// path from that prefab's root. Encoded in the GameObject name, as the
    /// humanoid mirror's reference sentinel is, because Unity's bundle
    /// serialiser handles plain GameObject names natively and needs no
    /// per-mod runtime assembly to carry them.
    /// </summary>
    private const string SentinelPrefix = "__jiangyu_scripts:";

    /// <summary>
    /// Sentinel rename once a sub-assembly has been through the mirror,
    /// successfully or not. Distinguishes "still waiting for the vanilla
    /// prefab to load" from "already handled", and survives
    /// Object.Instantiate so live clones inherit the marker.
    /// </summary>
    private const string RestoredSentinelPrefix = "__jiangyu_scripts_done:";

    /// <summary>
    /// True when <paramref name="addition"/> carries at least one
    /// sub-assembly sentinel, i.e. it opts into script restoration.
    /// </summary>
    public static bool HasSentinel(GameObject addition)
        => addition != null && FindSentinels(addition).Count > 0;

    /// <summary>
    /// Restore scripts on every marked sub-assembly of
    /// <paramref name="addition"/>. Returns true when every sentinel has
    /// been resolved one way or the other, false when at least one names a
    /// vanilla prefab that is not loaded yet, in which case the caller
    /// re-queues and retries on a later loader pass.
    /// <paramref name="label"/> names the addition in log lines: the
    /// registered bundle key, because the root Object.name is `main` for
    /// every conventionally laid out bundle. With
    /// <paramref name="warnWhenReferenceMissing"/> a still-unresolved
    /// reference is surfaced once, so a misspelt sentinel does not wait
    /// forever in silence.
    /// </summary>
    public static bool Mirror(
        GameObject addition, string label, MelonLogger.Instance log, bool warnWhenReferenceMissing = false)
    {
        var sentinels = FindSentinels(addition);
        if (sentinels.Count == 0) return true;

        var complete = true;
        foreach (var (sentinel, payload, referenceName, referencePath) in sentinels)
        {
            var reference = MirrorReferenceLookup.FindPrefab(referenceName, addition);
            if (reference == null)
            {
                // Keep the sentinel in place so the next pass finds it again
                // once MENACE's asset registry has the vanilla prefab.
                complete = false;
                if (warnWhenReferenceMissing)
                    log.Warning(
                        $"  Script restore on '{label}': vanilla prefab '{referenceName}' is not in the "
                        + "asset registry yet; retrying each apply pass. A name the game never loads "
                        + "waits forever — check the sentinel spelling.");
                continue;
            }

            var additionScope = sentinel.parent;
            var referenceScope = ResolveReferenceScope(
                reference, label, additionScope, referencePath, referenceName, log);

            if (referenceScope != null)
            {
                var result = Il2CppComponentMirror.Mirror(
                    referenceScope, additionScope, reference.transform, log, label);
                Report(label, referenceName, additionScope, result, log);
            }

            // Renamed either way. A reference that is loaded but has no
            // matching node will not start matching on a later pass, and
            // retrying would only repeat the warning every pass.
            sentinel.gameObject.name = RestoredSentinelPrefix + payload;
        }

        return complete;
    }

    private static void Report(
        string label,
        string referenceName,
        Transform additionScope,
        Il2CppComponentMirror.Result result,
        MelonLogger.Instance log)
    {
        // Only a pairing that found NOTHING is suspicious. Zero additions
        // with components already present is the overlap case: an enclosing
        // marker restored this node first, and warning here would point a
        // modder at a name mismatch that does not exist.
        if (result.ComponentsAdded == 0 && result.ComponentsAlreadyPresent == 0)
        {
            log.Warning(
                $"  Script restore on '{label}': node '{additionScope.name}' paired with "
                + $"'{referenceName}' but the vanilla side carries no scripts to restore "
                + $"({result.NodesPaired} node(s) paired). Check the node name matches vanilla.");
            return;
        }

        var extra = string.Empty;
        if (result.MaterialsRebound > 0)
            extra += $", {result.MaterialsRebound} material(s) rebound to vanilla";
        if (result.NodesUnmatched > 0)
            extra += $", {result.NodesUnmatched} vanilla node(s) absent from the copy";
        if (result.ComponentsAlreadyPresent > 0)
            extra += $", {result.ComponentsAlreadyPresent} left as authored";
        if (result.FieldsSkipped > 0)
            extra += $", {result.FieldsSkipped} oversized field(s) skipped";
        if (result.DanglingReferences > 0)
            extra += $", {result.DanglingReferences} reference(s) escaping the copy";

        // Reported unconditionally rather than behind the debug flag. A
        // marked sub-assembly is a rare, deliberate act by the modder, and
        // a silently unrestored one looks like a broken effect in-game with
        // nothing in the log to explain it.
        log.Msg(
            $"  Script restore on '{label}': node '{additionScope.name}' restored "
            + $"{result.ComponentsAdded} component(s) from '{referenceName}' across "
            + $"{result.NodesPaired} node(s){extra}.");
    }

    /// <summary>
    /// Find the node in the vanilla prefab that the marked addition node
    /// was copied from. An explicit path wins. A sentinel on the addition's
    /// own root pairs the two roots. Otherwise the vanilla prefab is
    /// searched for a single node sharing the marked node's name.
    /// </summary>
    private static Transform ResolveReferenceScope(
        GameObject reference,
        string label,
        Transform additionScope,
        string referencePath,
        string referenceName,
        MelonLogger.Instance log)
    {
        if (!string.IsNullOrEmpty(referencePath))
        {
            var atPath = reference.transform.Find(referencePath);
            if (atPath == null)
            {
                log.Warning(
                    $"  Script restore on '{label}': '{referenceName}' has no node at path "
                    + $"'{referencePath}'. Nothing restored on '{additionScope.name}'.");
            }
            return atPath;
        }

        if (additionScope.parent == null)
            return reference.transform;

        Transform found = null;
        var matches = 0;
        foreach (var candidate in reference.transform.GetComponentsInChildren<Transform>(true))
        {
            if (candidate == null || candidate.name != additionScope.name) continue;
            matches++;
            found ??= candidate;
        }

        if (matches == 0)
        {
            log.Warning(
                $"  Script restore on '{label}': '{referenceName}' has no node named "
                + $"'{additionScope.name}'. Rename the copied node back to its vanilla name, or "
                + "name the counterpart explicitly with an @path suffix on the sentinel.");
            return null;
        }
        if (matches > 1)
        {
            log.Warning(
                $"  Script restore on '{label}': '{referenceName}' has {matches} nodes named "
                + $"'{additionScope.name}'. Name the counterpart explicitly with an @path suffix on "
                + "the sentinel.");
            return null;
        }
        return found;
    }

    /// <summary>
    /// Every pending sentinel under <paramref name="addition"/>, with the
    /// vanilla prefab name and optional counterpart path parsed off it. A
    /// sentinel on the prefab root itself is ignored: the mirror pairs the
    /// sentinel's parent, and a root sentinel has no parent to pair.
    /// </summary>
    private static List<(Transform Sentinel, string Payload, string ReferenceName, string ReferencePath)> FindSentinels(
        GameObject addition)
    {
        var found = new List<(Transform, string, string, string)>();
        foreach (var node in addition.transform.GetComponentsInChildren<Transform>(true))
        {
            if (node == null || node.parent == null) continue;
            if (!TryParseSentinel(node.name, out var payload, out var referenceName, out var referencePath)) continue;
            found.Add((node, payload, referenceName, referencePath));
        }
        return found;
    }

    /// <summary>
    /// Split a pending sentinel's GameObject name into the vanilla prefab
    /// name and the optional counterpart path. False for any name that is
    /// not a pending sentinel, including the done marker and a sentinel
    /// naming no prefab at all.
    /// </summary>
    internal static bool TryParseSentinel(
        string name, out string payload, out string referenceName, out string referencePath)
    {
        payload = null;
        referenceName = null;
        referencePath = null;
        if (name == null || !name.StartsWith(SentinelPrefix, StringComparison.Ordinal)) return false;

        payload = name[SentinelPrefix.Length..];
        var separator = payload.IndexOf('@');
        referenceName = separator < 0 ? payload : payload[..separator];
        referencePath = separator < 0 || separator == payload.Length - 1 ? null : payload[(separator + 1)..];
        return referenceName.Length > 0;
    }
}
