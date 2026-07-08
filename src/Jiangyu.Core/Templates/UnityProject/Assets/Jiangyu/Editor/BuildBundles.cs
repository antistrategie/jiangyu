using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            var bundleNames = new List<string>();
            AssignBundleNames("t:Prefab", "Assets/Prefabs", ".prefab", bundleNames);
            AssignBundleNames("t:VisualTreeAsset", "Assets/UI", ".uxml", bundleNames);
            // Textures under Assets/UI/Icons get their own bundle, loadable by name via
            // Context.Assets.Load. Textures elsewhere under Assets/UI stay as UXML/USS
            // dependencies of their owning UXML. So put a texture here only if it is loaded
            // standalone: an Icons texture also referenced by a UXML is pulled out of that UXML's
            // bundle and the styled element loses its background image. Keep UXML-referenced
            // textures outside Assets/UI/Icons.
            AssignIconTextureBundles("Assets/UI/Icons", bundleNames);
            if (bundleNames.Count == 0)
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

            // ForceRebuildAssetBundle: never trust Unity's own incremental bundle cache. A stale
            // .manifest left in the output dir (e.g. a delete that a Windows file lock defeated)
            // otherwise makes Unity decide the bundle is current and skip it, emitting no file
            // while still succeeding. Jiangyu already skips the whole Unity invocation when its
            // inputs are unchanged, so a full rebuild here has no extra cost when Unity does run.
            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                Debug.LogError("Jiangyu BuildBundles: BuildAssetBundles returned null.");
                return false;
            }

            Debug.Log("Jiangyu BuildBundles: built " + manifest.GetAllAssetBundles().Length + " bundle(s) into " + outputDir);

            // A non-null manifest does not guarantee each expected bundle was written (a
            // cold-project import pass can leave assets unimported), so verify every assigned
            // bundle actually landed on disk.
            return BundleBuildVerify.AllWritten(outputDir, bundleNames, manifest, "Jiangyu BuildBundles");
        }

        /// <summary>
        /// Assign an AssetBundle to every asset of the matched type under <paramref name="root"/>,
        /// keyed by <paramref name="bundleKeyOf"/> applied to each asset's path under the root.
        /// Each assigned bundle name is recorded in <paramref name="into"/> for the caller to
        /// verify it was written, deduped case-insensitively because Unity lowercases
        /// <c>assetBundleName</c> on assignment (so two keys differing only in case land on one
        /// file). A missing root adds nothing.
        /// </summary>
        private static void AssignBundles(string filter, string root, string extension, ICollection<string> into, Func<string, string> bundleKeyOf)
        {
            if (!AssetDatabase.IsValidFolder(root))
                return;

            var rootPrefix = root.EndsWith("/") ? root : root + "/";
            foreach (var guid in AssetDatabase.FindAssets(filter, new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(extension))
                    continue;
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                    continue;

                var relative = path.StartsWith(rootPrefix) ? path.Substring(rootPrefix.Length) : Path.GetFileName(path);
                var bundleName = bundleKeyOf(relative) + ".bundle";
                importer.assetBundleName = bundleName;
                if (!into.Any(existing => string.Equals(existing, bundleName, StringComparison.OrdinalIgnoreCase)))
                    into.Add(bundleName);
            }
        }

        /// <summary>
        /// Give every asset of the matched type under <paramref name="root"/> its own AssetBundle,
        /// keyed by the asset's path under the root with the extension stripped and <c>/</c>
        /// translated to <c>__</c> (the KDL <c>asset="dir/name"</c> convention). A USS linked from
        /// a UXML by a <c>&lt;Style&gt;</c> tag rides inside that UXML's bundle as a dependency, so
        /// only the UXML is keyed.
        /// </summary>
        private static void AssignBundleNames(string filter, string root, string extension, ICollection<string> into)
            => AssignBundles(filter, root, extension, into,
                relative => relative.Substring(0, relative.Length - extension.Length).Replace("/", "__").Replace("\\", "__"));

        /// <summary>
        /// Textures under <paramref name="root"/> (<c>Assets/UI/Icons</c>), loadable standalone by
        /// name via <c>Context.Assets.Load</c>. A texture directly in the folder gets its own
        /// bundle keyed by its leaf name (<c>gift_icon.png</c> to <c>gift_icon.bundle</c>). A
        /// texture inside a subfolder is grouped into a single bundle keyed
        /// <c>&lt;Icons&gt;__&lt;subfolder&gt;</c>, so a sprite sequence (many frames) ships as one
        /// bundle (<c>Icons/campaign/**</c> to <c>Icons__campaign.bundle</c>) rather than a bundle
        /// per frame. The subfolder key keeps the <c>&lt;category&gt;__</c> prefix that the
        /// path-flattened prefab/UXML keys use, so a subfolder can never collide with a
        /// direct-texture leaf or another category's key. The loader resolves each asset by its own
        /// leaf or category-relative name, so the grouped bundle's file name does not affect loads.
        /// JIANGYU-CONTRACT: grouping a texture subfolder into one loadable bundle is a
        /// mod-authoring convention the loader's resolve-by-name relies on, proven for the WOMENACE
        /// campaign map.
        /// </summary>
        private static void AssignIconTextureBundles(string root, ICollection<string> into)
        {
            var prefix = root.Substring(root.LastIndexOf('/') + 1) + "__"; // e.g. "Icons__"
            AssignBundles("t:Texture2D", root, ".png", into, relative =>
            {
                var slash = relative.IndexOf('/');
                return slash >= 0
                    ? prefix + relative.Substring(0, slash)                    // subfolder: one grouped, namespaced bundle
                    : relative.Substring(0, relative.Length - ".png".Length);  // direct: own bundle by leaf
            });
        }
    }

    /// <summary>
    /// Shared post-build check for the three batchmode bundle builders. A non-null
    /// <see cref="AssetBundleManifest"/> does not guarantee the expected bundle files were
    /// written: a cold-project import pass can leave assets unimported, so Unity reports success
    /// yet emits nothing. Verify each expected bundle actually landed on disk. Unity lowercases
    /// <c>assetBundleName</c> on assignment, so the written file is the lowercased key. Logs and
    /// returns false naming the gap otherwise, so the compiler surfaces this rather than the
    /// opaque downstream "did not produce expected bundle".
    /// </summary>
    internal static class BundleBuildVerify
    {
        public static bool AllWritten(string outputDir, IEnumerable<string> expectedBundleNames, AssetBundleManifest manifest, string label)
        {
            var missing = new List<string>();
            foreach (var name in expectedBundleNames)
            {
                var normalised = name.ToLowerInvariant();
                if (!File.Exists(Path.Combine(outputDir, normalised)))
                    missing.Add(normalised);
            }
            if (missing.Count == 0)
                return true;

            var built = manifest != null ? manifest.GetAllAssetBundles() : new string[0];
            var listing = Directory.Exists(outputDir)
                ? string.Join(", ", Directory.GetFiles(outputDir).Select(Path.GetFileName))
                : "(output dir missing)";
            Debug.LogError(
                label + ": BuildAssetBundles reported success but did not write expected bundle(s) [" +
                string.Join(", ", missing) + "] to '" + outputDir + "'. Bundles in manifest: [" +
                string.Join(", ", built) + "]. Files present: [" + listing + "].");
            return false;
        }
    }
}
