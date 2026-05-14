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
                    var removed = StripRecursive(root);
                    if (removed > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        Debug.Log("Jiangyu: stripped " + removed + " missing-script component(s) from '" + path + "'");
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

        private static int StripRecursive(GameObject go)
        {
            var removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            foreach (Transform child in go.transform)
                removed += StripRecursive(child.gameObject);
            return removed;
        }
    }
}
