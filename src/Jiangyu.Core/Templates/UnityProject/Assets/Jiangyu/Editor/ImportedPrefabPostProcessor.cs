using UnityEditor;
using UnityEngine;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Strips missing-script <c>MonoBehaviour</c> components from prefabs
    /// imported under <c>Assets/Imported/</c>. AssetRipper extractions
    /// preserve component references into game scripts (Menace.*) that don't
    /// exist in the modder's Unity project, so by default Unity refuses to
    /// save these prefabs ("missing script" error). The runtime behaviour
    /// from those scripts isn't reproducible outside MENACE anyway; the
    /// modded prefab's visual and structural identity is what matters for
    /// bundling. This postprocessor strips the missing components on import
    /// so the modder never sees the error.
    /// </summary>
    public sealed class ImportedPrefabPostProcessor : AssetPostprocessor
    {
        private const string TargetRoot = "Assets/Imported/";

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets)
            {
                if (!path.StartsWith(TargetRoot)) continue;
                if (!path.EndsWith(".prefab")) continue;

                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                    var stamped = 0;
                    var removed = StripRecursive(root, root, ref stamped);
                    if (removed > 0 || stamped > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        Debug.Log("Jiangyu: stripped " + removed + " missing-script component(s), stamped "
                            + stamped + " script marker(s) on '" + path + "'");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("Jiangyu: failed to post-process '" + path + "': " + ex.Message);
                }
                finally
                {
                    if (root != null)
                        PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        // Each node that had scripts stripped gets a __jiangyu_scripts marker
        // child, so a sub-assembly copied out of the import carries its markers
        // with it and the loader restores those scripts from the live vanilla
        // prefab at load time. The marker names the counterpart explicitly by
        // path from the vanilla prefab's root, so pairing survives node renames
        // and repeated names. Delete a marker to keep a copy scripts-free.
        private static int StripRecursive(GameObject go, GameObject root, ref int stamped)
        {
            var own = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            var removed = own;
            foreach (Transform child in go.transform)
            {
                if (child.name.StartsWith("__jiangyu_scripts", System.StringComparison.Ordinal)) continue;
                removed += StripRecursive(child.gameObject, root, ref stamped);
            }
            if (own > 0 && !HasMarker(go))
            {
                var marker = new GameObject(MarkerName(go, root));
                marker.transform.SetParent(go.transform, worldPositionStays: false);
                stamped++;
            }
            return removed;
        }

        private static bool HasMarker(GameObject go)
        {
            foreach (Transform child in go.transform)
                if (child.name.StartsWith("__jiangyu_scripts", System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static string MarkerName(GameObject go, GameObject root)
        {
            if (go == root)
                return "__jiangyu_scripts:" + root.name;
            var path = go.name;
            for (var node = go.transform.parent; node != null && node.gameObject != root; node = node.parent)
                path = node.name + "/" + path;
            return "__jiangyu_scripts:" + root.name + "@" + path;
        }
    }
}
