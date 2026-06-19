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
    /// Output goes to <c>&lt;modRoot&gt;/.jiangyu/unity_build/</c> (sibling of
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

        public static void BuildAll()
        {
            EditorApplication.Exit(RunCore() ? 0 : 1);
        }

        /// <summary>
        /// Builds every prefab under Assets/Prefabs/ into its own AssetBundle.
        /// Returns true on success (including the "no prefabs to build" case)
        /// and false on a build error. Does not call EditorApplication.Exit so
        /// the caller may chain additional passes in the same Unity batchmode
        /// session.
        /// </summary>
        public static bool RunCore()
        {
            if (Application.unityVersion != ExpectedUnityVersion)
            {
                Debug.LogError(
                    "Jiangyu BuildBundles: Unity version mismatch. " +
                    "Expected " + ExpectedUnityVersion + ", got " + Application.unityVersion + ". " +
                    "Open this project in the matching Unity Editor before building.");
                return false;
            }

            var assigned =
                AssignBundleNames("t:Prefab", "Assets/Prefabs", ".prefab") +
                AssignBundleNames("t:VisualTreeAsset", "Assets/UI", ".uxml") +
                // Textures under Assets/UI/Icons get their own bundle, loadable by name via
                // Context.Assets.Load. Textures elsewhere under Assets/UI stay as UXML/USS
                // dependencies of their owning UXML. So put a texture here only if it is loaded
                // standalone: an Icons texture also referenced by a UXML is pulled out of that UXML's
                // bundle and the styled element loses its background image. Keep UXML-referenced
                // textures outside Assets/UI/Icons.
                AssignBundleNames("t:Texture2D", "Assets/UI/Icons", ".png");
            if (assigned == 0)
            {
                Debug.LogWarning("Jiangyu BuildBundles: no prefabs under Assets/Prefabs/ or UXML under Assets/UI/. Nothing to build.");
                return true;
            }
            AssetDatabase.SaveAssets();

            // unity/ is <modRoot>/unity, so its parent is the mod root.
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var modRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            var outputDir = Path.Combine(modRoot, ".jiangyu", "unity_build");
            Directory.CreateDirectory(outputDir);

            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                Debug.LogError("Jiangyu BuildBundles: BuildAssetBundles returned null.");
                return false;
            }

            var built = manifest.GetAllAssetBundles();
            Debug.Log("Jiangyu BuildBundles: built " + built.Length + " bundle(s) into " + outputDir);
            return true;
        }

        /// <summary>
        /// Give every asset of the matched type under <paramref name="root"/> its own
        /// AssetBundle, keyed by the asset's path under the root with the extension
        /// stripped and <c>/</c> translated to <c>__</c> (the KDL <c>asset="dir/name"</c>
        /// convention). A USS linked from a UXML by a <c>&lt;Style&gt;</c> tag rides
        /// inside that UXML's bundle as a dependency, so only the UXML is keyed.
        /// Returns the number of assets assigned. A missing root contributes zero.
        /// </summary>
        private static int AssignBundleNames(string filter, string root, string extension)
        {
            if (!AssetDatabase.IsValidFolder(root))
                return 0;

            var rootPrefix = root.EndsWith("/") ? root : root + "/";
            var guids = AssetDatabase.FindAssets(filter, new[] { root });
            var count = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(extension))
                    continue;
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                    continue;

                var relative = path.StartsWith(rootPrefix)
                    ? path.Substring(rootPrefix.Length)
                    : Path.GetFileName(path);
                var stem = relative.Substring(0, relative.Length - extension.Length);
                var bundleKey = stem.Replace("/", "__").Replace("\\", "__");
                importer.assetBundleName = bundleKey + ".bundle";
                count++;
            }
            return count;
        }
    }
}
