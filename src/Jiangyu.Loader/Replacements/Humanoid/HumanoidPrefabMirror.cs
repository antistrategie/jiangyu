using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Il2CppMenace.Tactical;
using MenaceFootprints = Il2CppMenace.Tactical.Footprints;
using MenaceRagdoll = Il2CppMenace.Tactical.Ragdoll;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Brings a humanoid addition prefab's MonoBehaviour configuration up
/// to parity with a canonical reference soldier prefab from the game's
/// data. AssetRipper strips MonoBehaviour field payloads when
/// extracting the reference soldier for the bake pipeline, so the
/// addition prefab reaches the loader with the components attached
/// (BakeHumanoid re-attaches them) but no field data. The runtime
/// mirror walks each component's serialised fields, copies primitives
/// and asset references verbatim, and remaps in-prefab Transform
/// references via relative path on the addition's bone hierarchy.
///
/// Mirror layers, applied in order:
/// <list type="bullet">
///   <item><description>Ragdoll: bind <c>m_Template</c> to the reference's
///     RagdollTemplate (typically <c>ragdoll_human</c>) and remap
///     <c>m_SkeletalRoot</c> onto the addition's bone hierarchy.</description></item>
///   <item><description>Footprints: copy <c>m_Foots[]</c> + <c>ChanceToSkipStep</c>
///     entry-by-entry, remapping each <c>FootConfig.Transform</c>
///     onto the addition's bones and shallow-copying
///     <c>FootConfig.Decals</c> (a shared SurfaceDecalsTemplate
///     ScriptableObject).</description></item>
/// </list>
///
/// GameObject-hierarchy mirroring (footstep dust spawn containers,
/// audio markers, anything else under the reference root that's not a
/// bone or LOD mesh) happens at bake time inside
/// <c>BakeHumanoid.CopySupplementaryChildrenFromReference</c>, not
/// here. Unity treats runtime-loaded bundle prefabs as structurally
/// immutable for live <c>SetParent</c> ops; children attached via
/// runtime <c>Object.Instantiate</c> end up orphaned, so the bake
/// step is the only place those structural copies actually survive.
///
/// Reference prefab identity is carried on the addition via a
/// <see cref="ReferenceSentinelPrefix"/>-prefixed child GameObject the
/// bake pipeline writes. The sentinel is renamed (not destroyed) on
/// successful mirror so a later scan can tell mirrored prefabs from
/// un-mirrored ones at a glance.
/// </summary>
internal static class HumanoidPrefabMirror
{
    /// <summary>
    /// Name prefix on the sentinel child GameObject the bake pipeline
    /// writes onto humanoid addition prefabs. The remainder of the
    /// child's name is the reference vanilla prefab's runtime
    /// Object.name. Encoded in the GameObject name (rather than via a
    /// custom MonoBehaviour) because Unity's bundle serialiser handles
    /// plain GameObject names natively — no per-mod runtime assembly
    /// needed.
    /// </summary>
    private const string ReferenceSentinelPrefix = "__jiangyu_ref:";

    /// <summary>
    /// Sentinel rename after a successful mirror. Lets us distinguish
    /// "not yet mirrored" (original prefix) from "already mirrored"
    /// (this prefix) so the periodic instance scan doesn't redo work.
    /// Survives Object.Instantiate, so live clones of an already-
    /// mirrored prefab inherit the done marker.
    /// </summary>
    private const string MirroredSentinelPrefix = "__jiangyu_mirrored:";

    /// <summary>
    /// Returns true when <paramref name="addition"/> is shaped like a
    /// humanoid soldier prefab the mirror can do useful work on: has an
    /// Animator with a humanoid avatar. Geometry-only props and vehicle
    /// shells skip the mirror.
    /// </summary>
    public static bool IsHumanoid(GameObject addition)
    {
        if (addition == null) return false;
        var animator = addition.GetComponent<Animator>();
        return animator != null && animator.avatar != null && animator.avatar.isHuman;
    }

    /// <summary>
    /// Mirror reference data onto <paramref name="addition"/>. The
    /// reference vanilla prefab is identified by the sentinel child
    /// GameObject the bake pipeline embeds (see
    /// <see cref="ReferenceSentinelPrefix"/>). On success the sentinel
    /// is renamed to <see cref="MirroredSentinelPrefix"/>+ref so a
    /// later instance scan can tell the difference between "needs
    /// mirroring" and "already mirrored" (instances inherit the
    /// sentinel from their prefab). Returns true when the mirror ran
    /// to completion (either configured, or no-op because the addition
    /// opted out by omitting the sentinel); false when the sentinel
    /// was present but the named reference isn't loaded yet — caller
    /// should re-queue and retry during a later loader pass.
    /// </summary>
    public static bool Mirror(GameObject addition, MelonLogger.Instance log)
    {
        var (sentinel, referenceName) = FindReferenceSentinel(addition);
        if (string.IsNullOrEmpty(referenceName))
        {
            // Already-mirrored (sentinel rewritten) or non-humanoid
            // prefab. Either way, nothing to do.
            return true;
        }

        var reference = FindReferencePrefab(referenceName);
        if (reference == null)
        {
            // Not loaded yet — caller re-queues us until ApplyReplacements
            // catches a pass where MENACE's asset registry includes the
            // reference. Keep the sentinel in place so the next pass can
            // find it again.
            return false;
        }

        MirrorRagdoll(addition, reference, log);
        MirrorFootprints(addition, reference, log);
        // Note: GameObject-hierarchy mirroring (dust spawn containers,
        // audio markers, anything else under the reference root that's
        // not a bone or LOD mesh) happens at bake time inside
        // BakeHumanoid, not here. Unity treats runtime-loaded bundle
        // prefabs as structurally immutable for live SetParent ops —
        // children added via Object.Instantiate at runtime end up
        // orphaned. MonoBehaviour fields are the runtime-only thing
        // (Il2CppInterop wrappers don't exist at bake time), so the
        // runtime mirror is scoped to those.

        // Rename the sentinel rather than destroying it so a follow-up
        // pass can tell mirrored prefabs from un-mirrored ones at a
        // glance. Clones inherit the renamed sentinel, so they don't
        // get picked up as needing work either.
        if (sentinel != null)
            sentinel.gameObject.name = MirroredSentinelPrefix + referenceName;

        log.Msg(
            $"  Humanoid mirror on '{addition.name}': configured from reference '{referenceName}'.");
        return true;
    }

    /// <summary>
    /// Looks up a vanilla prefab in Unity's loaded asset registry by
    /// runtime Object.name. Returns null when the named prefab isn't
    /// loaded yet, so callers can re-queue. Filters out scene
    /// instantiations: we only mirror from clean prefab assets, never
    /// from live scene objects whose state is mid-frame.
    /// </summary>
    private static GameObject FindReferencePrefab(string name)
    {
        var gameObjects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
        foreach (var obj in gameObjects)
        {
            if (obj == null) continue;
            var go = obj.TryCast<GameObject>();
            if (go == null) continue;
            if (go.scene.handle != 0) continue;
            if (go.name == name) return go;
        }
        return null;
    }

    private static (Transform Sentinel, string ReferenceName) FindReferenceSentinel(GameObject addition)
    {
        var root = addition.transform;
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child == null) continue;
            var n = child.name;
            if (n != null && n.StartsWith(ReferenceSentinelPrefix, StringComparison.Ordinal))
                return (child, n.Substring(ReferenceSentinelPrefix.Length));
        }
        return (null, null);
    }

    private static void MirrorRagdoll(GameObject addition, GameObject reference, MelonLogger.Instance log)
    {
        var src = reference.GetComponent<MenaceRagdoll>();
        if (src == null)
        {
            log.Warning(
                $"  Humanoid mirror on '{addition.name}': reference '{reference.name}' has no "
                + "MenaceRagdoll component. Death physics will not activate on this unit.");
            return;
        }
        var dst = addition.GetComponent<MenaceRagdoll>() ?? addition.AddComponent<MenaceRagdoll>();
        dst.m_Template = src.m_Template;
        dst.m_SkeletalRoot = RemapTransform(src.m_SkeletalRoot, reference.transform, addition.transform, log);
        MirrorRagdollParts(dst, src, addition, log);
    }

    /// <summary>
    /// Copy the reference Ragdoll's <c>m_Parts</c> array onto the addition
    /// Ragdoll, remapping each <c>RagdollPart.Rigidbody</c> reference from
    /// the reference's bone Rigidbody to the addition's same-named bone
    /// Rigidbody. Static config on each part (HasCustomCenterOfMass,
    /// AttachmentSlot, DismemberPrefab, etc.) is preserved.
    ///
    /// MENACE serialises <c>m_Parts</c> on the live soldier prefab. When
    /// the runtime instantiates an addition prefab without this populated,
    /// <c>Ragdoll.GetCenterRigidbody()</c> returns null and the death
    /// physics chain bombs out.
    /// </summary>
    private static void MirrorRagdollParts(
        MenaceRagdoll dst, MenaceRagdoll src,
        GameObject addition, MelonLogger.Instance log)
    {
        var srcParts = src.m_Parts;
        if (srcParts == null || srcParts.Count == 0)
        {
            log.Warning(
                $"  Humanoid mirror on '{addition.name}': reference Ragdoll has empty m_Parts; "
                + "death physics will not activate.");
            return;
        }

        var bakedRigidbodies = new Dictionary<string, Rigidbody>(StringComparer.Ordinal);
        foreach (var rb in addition.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            bakedRigidbodies[rb.gameObject.name] = rb;

        var remapped = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<RagdollPart>(srcParts.Count);
        for (int i = 0; i < srcParts.Count; i++)
        {
            var srcPart = srcParts[i];
            if (srcPart == null || srcPart.Rigidbody == null)
                continue;
            var boneName = srcPart.Rigidbody.gameObject.name;
            if (!bakedRigidbodies.TryGetValue(boneName, out var dstRb))
            {
                log.Warning(
                    $"  Humanoid mirror on '{addition.name}': RagdollPart[{i}] bone '{boneName}' "
                    + "has no equivalent Rigidbody in the baked rig; part skipped.");
                continue;
            }

            remapped[i] = new RagdollPart
            {
                Rigidbody = dstRb,
                HasCustomCenterOfMass = srcPart.HasCustomCenterOfMass,
                CustomCenterOfMass = srcPart.CustomCenterOfMass,
                DismemberPrefab = srcPart.DismemberPrefab,
                CanHaveAttachmentWithCollider = srcPart.CanHaveAttachmentWithCollider,
                AttachmentSlot = srcPart.AttachmentSlot,
            };
        }

        dst.m_Parts = remapped;
    }

    private static void MirrorFootprints(GameObject addition, GameObject reference, MelonLogger.Instance log)
    {
        var src = reference.GetComponent<MenaceFootprints>();
        if (src == null) return;
        var dst = addition.GetComponent<MenaceFootprints>() ?? addition.AddComponent<MenaceFootprints>();
        dst.ChanceToSkipStep = src.ChanceToSkipStep;

        var srcFoots = src.m_Foots;
        if (srcFoots == null || srcFoots.Length == 0)
        {
            dst.m_Foots = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FootConfig>(0);
            return;
        }

        var copied = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FootConfig>(srcFoots.Length);
        for (var i = 0; i < srcFoots.Length; i++)
        {
            var srcFoot = srcFoots[i];
            if (srcFoot == null) continue;
            var dstFoot = new FootConfig
            {
                Transform = RemapTransform(srcFoot.Transform, reference.transform, addition.transform, log),
                Decals = srcFoot.Decals,
            };
            copied[i] = dstFoot;
        }
        dst.m_Foots = copied;
    }

    /// <summary>
    /// Maps a Transform reference rooted at <paramref name="sourceRoot"/>
    /// to the equivalent Transform under <paramref name="targetRoot"/>
    /// using the relative path. Returns null when the source transform
    /// isn't a descendant of <paramref name="sourceRoot"/> or when the
    /// equivalent target path can't be found. Logs a warning on the
    /// "not found in target" branch since that points at a real
    /// skeleton-mapping mismatch worth surfacing.
    /// </summary>
    private static Transform RemapTransform(
        Transform source, Transform sourceRoot, Transform targetRoot, MelonLogger.Instance log)
    {
        if (source == null) return null;
        if (source == sourceRoot) return targetRoot;

        var path = RelativePath(source, sourceRoot);
        if (path == null)
        {
            // Source transform isn't under sourceRoot's hierarchy.
            // Could be a cross-prefab reference; copy as-is and let
            // Unity resolve at runtime.
            return source;
        }
        var resolved = targetRoot.Find(path);
        if (resolved == null)
        {
            log.Warning(
                $"  Humanoid mirror on '{targetRoot.name}': could not resolve transform '{path}' on target; "
                + "leaving Transform reference null.");
            return null;
        }
        return resolved;
    }

    private static string RelativePath(Transform descendant, Transform root)
    {
        if (descendant == root) return string.Empty;
        var segments = new List<string>();
        var current = descendant;
        while (current != null && current != root)
        {
            segments.Add(current.name);
            current = current.parent;
        }
        if (current == null) return null;
        segments.Reverse();
        return string.Join("/", segments);
    }

}
