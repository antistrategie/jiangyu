using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Bone-by-name traversal helpers used by both the direct mesh applier
/// (rebinding an SMR's bones against the host skeleton) and the driven
/// prefab manager (mapping its own skeleton onto the host's). The two
/// callers diverge in how they seed the map, but the recursive walk and
/// the diagnostic vector format are identical.
/// </summary>
internal static class SkeletonTraversal
{
    public static void CollectBonesRecursive(Transform parent, Dictionary<string, Transform> map)
    {
        if (parent == null)
            return;

        map.TryAdd(parent.name, parent);
        for (int i = 0; i < parent.childCount; i++)
            CollectBonesRecursive(parent.GetChild(i), map);
    }

    public static Dictionary<string, Transform> BuildTransformMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
        CollectBonesRecursive(root, map);
        return map;
    }

    public static string FormatVector3(Vector3 v)
        => $"({v.x:F4}, {v.y:F4}, {v.z:F4})";
}
