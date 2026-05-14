using System.IO;
using UnityEditor;
using UnityEngine;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Batchmode entry invoked by <c>mise compile</c> (or <c>jiangyu compile</c>)
    /// to build every prefab under <c>Assets/Prefabs/</c> into its own
    /// AssetBundle. The bundle name mirrors the KDL <c>asset="dir/name"</c>
    /// convention: relative path under <c>Assets/Prefabs/</c> with the
    /// <c>.prefab</c> extension stripped and <c>/</c> translated to <c>__</c>.
    /// So <c>Assets/Prefabs/dir/test_cube.prefab</c> becomes
    /// <c>dir__test_cube.bundle</c>, and a KDL reference to
    /// <c>asset="dir/test_cube"</c> resolves at runtime against
    /// <c>BundleReplacementCatalog.AdditionPrefabs</c>.
    ///
    /// Output goes to <c>&lt;modRoot&gt;/.jiangyu/unity-build/</c> (sibling of
    /// <c>unity/</c>) where the Jiangyu compile pipeline picks them up.
    ///
    /// Invoked as:
    /// <code>
    ///   Unity -batchmode -projectPath unity/ \
    ///     -executeMethod Jiangyu.Mod.BuildBundles.BuildAll -quit
    /// </code>
    /// </summary>
    public static class BuildBundles
    {
        private const string ExpectedUnityVersion = "6000.0.72f1";
        private const string PrefabsRoot = "Assets/Prefabs/";

        public static void BuildAll()
        {
            if (Application.unityVersion != ExpectedUnityVersion)
            {
                Debug.LogError(
                    "Jiangyu BuildBundles: Unity version mismatch. " +
                    "Expected " + ExpectedUnityVersion + ", got " + Application.unityVersion + ". " +
                    "Open this project in the matching Unity Editor before building.");
                EditorApplication.Exit(1);
                return;
            }

            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" });
            if (prefabGuids.Length == 0)
            {
                Debug.LogWarning("Jiangyu BuildBundles: no prefabs found under Assets/Prefabs/. Nothing to build.");
                EditorApplication.Exit(0);
                return;
            }

            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null) continue;

                var relative = path.StartsWith(PrefabsRoot)
                    ? path.Substring(PrefabsRoot.Length)
                    : Path.GetFileName(path);
                var stem = relative.Substring(0, relative.Length - ".prefab".Length);
                var bundleKey = stem.Replace("/", "__").Replace("\\", "__");
                importer.assetBundleName = bundleKey + ".bundle";
            }
            AssetDatabase.SaveAssets();

            // unity/ is <modRoot>/unity, so its parent is the mod root.
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var modRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            var outputDir = Path.Combine(modRoot, ".jiangyu", "unity-build");
            Directory.CreateDirectory(outputDir);

            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                Debug.LogError("Jiangyu BuildBundles: BuildAssetBundles returned null.");
                EditorApplication.Exit(1);
                return;
            }

            var built = manifest.GetAllAssetBundles();
            Debug.Log("Jiangyu BuildBundles: built " + built.Length + " bundle(s) into " + outputDir);
            EditorApplication.Exit(0);
        }
    }
}
