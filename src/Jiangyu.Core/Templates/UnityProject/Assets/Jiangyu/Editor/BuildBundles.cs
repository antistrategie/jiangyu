using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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

        // Bundles must be built for the target whose graphics API the game runs
        // on. MENACE runs through Proton and DXVK, so its API is D3D11, and a
        // bundle built for another target compiles its shaders for that
        // target's APIs instead. Shaders a mod ships then have no variant the
        // runtime can load and render magenta. The extraction stubs hide this,
        // because the loader rebinds those by name to the game's own compiled
        // shaders and their variants are never used.
        private const BuildTarget ExpectedBuildTarget = BuildTarget.StandaloneWindows64;
        private const GraphicsDeviceType RequiredGraphicsApi = GraphicsDeviceType.Direct3D11;

        public static void BuildAll()
        {
            if (!RunCore())
            {
                EditorApplication.Exit(1);
                return;
            }

            // Written last: the compile pipeline treats a fresh marker carrying its token
            // as the only proof this script ran to completion, since the bundle files
            // themselves persist across compiles as incremental state.
            var completionToken = GetArg(Environment.GetCommandLineArgs(), "-completionToken");
            if (!string.IsNullOrEmpty(completionToken))
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var modRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
                File.WriteAllText(Path.Combine(modRoot, ".jiangyu", "unity_build_prefabs.done"), completionToken);
            }
            EditorApplication.Exit(0);
        }

        private static string GetArg(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return args[i + 1];
            return null;
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

            if (!EnsureBuildTarget())
                return false;

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

            // A bundle whose prefab, UXML, or icon no longer exists must not linger for
            // staging to re-ship, and its stale .manifest must not confuse the incremental
            // build. Everything still assigned keeps its file and manifest: that is the
            // incremental state that lets an unchanged prefab's bundle skip rebuilding.
            // Extensionless replacement bundle files are not touched.
            PruneStaleBundles(outputDir, bundleNames);

            // Let Unity's own per-bundle hashing decide what to rebuild. A mod with many
            // prefabs (dozens of character rigs) pays real time here, and rebuilding every
            // bundle because one prefab moved is most of a compile. Jiangyu's fingerprint
            // gates whether this pass runs at all, but it cannot tell WHICH bundle changed,
            // and Unity can.
            //
            // The hazard ForceRebuildAssetBundle used to paper over is a stale .manifest
            // surviving in the output dir (e.g. a delete a file lock defeated): Unity then
            // decides the bundle is current, skips it, and emits no file while still
            // reporting success. That is exactly what AllWritten detects, so the recovery
            // is one forced retry rather than forcing every build.
            var manifest = BuildIncrementalThenForceOnGap(outputDir, bundleNames);
            if (manifest == null)
                return false;

            Debug.Log("Jiangyu BuildBundles: built " + manifest.GetAllAssetBundles().Length + " bundle(s) into " + outputDir);
            return true;
        }

        private static void PruneStaleBundles(string outputDir, List<string> expected)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in expected)
                live.Add(name);
            foreach (var file in Directory.GetFiles(outputDir))
            {
                var name = Path.GetFileName(file);
                var stem = name.EndsWith(".manifest", StringComparison.Ordinal)
                    ? name.Substring(0, name.Length - ".manifest".Length)
                    : name;
                if (!stem.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (live.Contains(stem))
                    continue;
                File.Delete(file);
            }
        }

        /// <summary>
        /// Put the editor on <see cref="ExpectedBuildTarget"/> and confirm that
        /// target compiles the graphics API the game runs on. Returns false when
        /// the build must not proceed.
        ///
        /// The <c>-buildTarget</c> command-line flag is not enough on its own:
        /// Unity ignores it when the platform module is absent, leaving the
        /// editor on its own platform and silently building bundles whose
        /// shaders carry the wrong API.
        /// </summary>
        private static bool EnsureBuildTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget != ExpectedBuildTarget)
            {
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, ExpectedBuildTarget))
                {
                    Debug.LogError(
                        "Jiangyu BuildBundles: this editor cannot build for " + ExpectedBuildTarget
                        + ", so its module is missing. Active target is "
                        + EditorUserBuildSettings.activeBuildTarget
                        + ", whose shader variants the game cannot load. Install the matching "
                        + "build-support module through Unity Hub (Installs, the gear on "
                        + ExpectedUnityVersion + ", Add modules) and build again.");
                    return false;
                }

                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.Standalone, ExpectedBuildTarget))
                {
                    Debug.LogError(
                        "Jiangyu BuildBundles: could not switch the active build target from "
                        + EditorUserBuildSettings.activeBuildTarget + " to " + ExpectedBuildTarget
                        + ". Set it in Build Settings and build again.");
                    return false;
                }

                Debug.Log("Jiangyu BuildBundles: switched the active build target to " + ExpectedBuildTarget + ".");
            }

            // Auto graphics APIs give Direct3D11 for a Windows target. An
            // explicit list that leaves it out ships variants the game cannot
            // load, which is the same failure by a different route.
            var apis = PlayerSettings.GetGraphicsAPIs(ExpectedBuildTarget);
            if (apis == null || !apis.Contains(RequiredGraphicsApi))
            {
                Debug.LogError(
                    "Jiangyu BuildBundles: " + ExpectedBuildTarget + " is set to build "
                    + (apis == null || apis.Length == 0 ? "no graphics API" : string.Join(", ", apis))
                    + ", which excludes " + RequiredGraphicsApi
                    + ". Shaders a mod ships would have no variant the game can load. Enable "
                    + RequiredGraphicsApi + " for this target in Player Settings, or turn "
                    + "Auto Graphics API back on.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Build the assigned bundles incrementally, and when any expected bundle is missing
        /// afterwards, rebuild once with <c>ForceRebuildAssetBundle</c>. Returns the manifest
        /// of the build that produced a complete output set, or null when even the forced
        /// rebuild left a gap (already logged by <see cref="BundleBuildVerify"/>).
        /// </summary>
        private static AssetBundleManifest BuildIncrementalThenForceOnGap(string outputDir, List<string> bundleNames)
        {
            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);

            // A null manifest is a build error, not a stale-cache gap: a forced retry would
            // only repeat it.
            if (manifest == null)
            {
                Debug.LogError("Jiangyu BuildBundles: BuildAssetBundles returned null.");
                return null;
            }

            if (BundleBuildVerify.AllWritten(outputDir, bundleNames, manifest, "Jiangyu BuildBundles (incremental)"))
                return manifest;

            Debug.LogWarning(
                "Jiangyu BuildBundles: incremental build left expected bundle(s) unwritten, " +
                "retrying with ForceRebuildAssetBundle.");

            manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                Debug.LogError("Jiangyu BuildBundles: forced BuildAssetBundles returned null.");
                return null;
            }

            return BundleBuildVerify.AllWritten(outputDir, bundleNames, manifest, "Jiangyu BuildBundles (forced)")
                ? manifest
                : null;
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
