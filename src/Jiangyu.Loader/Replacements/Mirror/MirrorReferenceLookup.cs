using Il2CppInterop.Runtime;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Resolves the vanilla prefab a mirror copies from, by runtime
/// Object.name, out of Unity's loaded asset registry.
///
/// <para>Two filters make the answer trustworthy. Scene instantiations
/// are excluded so a mirror only ever reads a clean prefab asset rather
/// than a live object whose state is mid-frame. The addition prefab and
/// its own children are excluded so an addition that carries a node
/// named after its reference (a glTF export keeping the vanilla root
/// name, for instance) cannot match itself and mirror from an empty
/// source.</para>
/// </summary>
internal static class MirrorReferenceLookup
{
    /// <summary>
    /// Find the loaded prefab asset named <paramref name="name"/>, ignoring
    /// anything inside <paramref name="addition"/>. Returns null when the
    /// named prefab is not loaded yet, so callers can re-queue and retry on
    /// a later loader pass.
    /// </summary>
    public static GameObject FindPrefab(string name, GameObject addition)
    {
        if (string.IsNullOrEmpty(name)) return null;

        Transform additionRoot = null;
        if (addition != null) additionRoot = addition.transform;

        var gameObjects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
        foreach (var obj in gameObjects)
        {
            if (obj == null) continue;
            var candidate = obj.TryCast<GameObject>();
            if (candidate == null) continue;
            if (candidate.scene.handle != 0) continue;
            if (candidate.name != name) continue;
            if (additionRoot != null && candidate.transform.IsChildOf(additionRoot)) continue;
            return candidate;
        }
        return null;
    }
}
