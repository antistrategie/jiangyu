using System.IO;
using UnityEditor;
using UnityEngine;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Batchmode entry that converts raw model files (.glb / .fbx) staged
    /// under <c>Assets/Jiangyu/Staging/Models/</c> into prefab AssetBundles,
    /// one bundle per model. Equivalent to the prefab pipeline in
    /// <see cref="BuildBundles"/>, but for modders who drop raw model files
    /// instead of authoring prefabs in-Editor.
    ///
    /// Output goes to <c>&lt;modRoot&gt;/.jiangyu/unity_build/</c> (same
    /// location as <see cref="BuildBundles"/>).
    ///
    /// Invoked as:
    /// <code>
    ///   Unity -batchmode -projectPath unity/ \
    ///     -executeMethod Jiangyu.Mod.BuildModelBundles.BuildAll \
    ///     -bundleName &lt;name&gt; -quit
    /// </code>
    /// </summary>
    public static class BuildModelBundles
    {
        private const string StagingDir = "Assets/Jiangyu/Staging/Models";
        private const string GeneratedPrefabDir = "Assets/Jiangyu/Staging/GeneratedPrefabs";
        private const float SoldierBoneTranslationScaleFix = 100f;

        public static void BuildAll()
        {
            var args = System.Environment.GetCommandLineArgs();
            var bundleName = GetArg(args, "-bundleName") ?? "mod";

            Directory.CreateDirectory(StagingDir);
            if (Directory.Exists(GeneratedPrefabDir))
                Directory.Delete(GeneratedPrefabDir, true);
            Directory.CreateDirectory(GeneratedPrefabDir);
            AssetDatabase.Refresh();

            var modelGuids = AssetDatabase.FindAssets("", new[] { StagingDir });
            var prefabCount = 0;

            foreach (var guid in modelGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(assetPath))
                    continue;

                var modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (modelImporter != null)
                {
                    var needsReimport = false;
                    if (modelImporter.animationType != ModelImporterAnimationType.Generic)
                    {
                        modelImporter.animationType = ModelImporterAnimationType.Generic;
                        needsReimport = true;
                    }
                    if (!modelImporter.isReadable)
                    {
                        modelImporter.isReadable = true;
                        needsReimport = true;
                    }
                    if (needsReimport)
                        modelImporter.SaveAndReimport();
                }

                var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (modelAsset == null)
                {
                    Debug.LogWarning("Jiangyu BuildModelBundles: could not load model: " + assetPath);
                    continue;
                }

                var prefabName = Path.GetFileNameWithoutExtension(assetPath);
                var prefabPath = GeneratedPrefabDir + "/" + prefabName + ".prefab";

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
                NormalizeCharacterPrefab(instance);
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                Object.DestroyImmediate(instance);

                var prefabImporter = AssetImporter.GetAtPath(prefabPath);
                if (prefabImporter != null)
                {
                    prefabImporter.assetBundleName = bundleName;
                    prefabCount++;
                }
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var modRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            var outputDir = Path.Combine(modRoot, ".jiangyu", "unity_build");
            Directory.CreateDirectory(outputDir);

            Debug.Log("Jiangyu BuildModelBundles: building bundle '" + bundleName + "' with " + prefabCount + " prefab(s)");

            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                Debug.LogError("Jiangyu BuildModelBundles: BuildAssetBundles returned null.");
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                    return args[i + 1];
            }
            return null;
        }

        // Soldier-rig fixups: flatten an extra self-named wrapper transform that
        // Blender's gltf exporter sometimes adds, scale the MENACE soldier
        // skeleton's local translations to game units, and rebind SMR rootBones
        // to Root/Hips. Only runs when the prefab actually carries the MENACE
        // humanoid skeleton (Root/Hips present). Non-soldier rigs and prop
        // models pass through untouched.
        private static void NormalizeCharacterPrefab(GameObject root)
        {
            if (root == null) return;

            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs == null || smrs.Length == 0) return;

            var skeletonOwner = FindSkeletonOwner(root.transform);
            if (skeletonOwner != null && skeletonOwner != root.transform)
            {
                var movedChildren = new System.Collections.Generic.List<Transform>();
                for (int i = 0; i < skeletonOwner.childCount; i++)
                    movedChildren.Add(skeletonOwner.GetChild(i));
                foreach (var child in movedChildren)
                    child.SetParent(root.transform, true);
                Object.DestroyImmediate(skeletonOwner.gameObject);
            }

            var hips = root.transform.Find("Root/Hips");
            if (hips == null)
                return;

            var rootBone = root.transform.Find("Root");
            if (rootBone != null)
                ScaleSkeletonLocalTranslations(rootBone, SoldierBoneTranslationScaleFix);

            foreach (var smr in smrs)
                smr.rootBone = hips;
        }

        private static Transform FindSkeletonOwner(Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child == null) continue;
                if (child.name == root.name)
                {
                    var rootDescendant = child.Find("Root");
                    if (rootDescendant != null) return child;
                }
            }
            return null;
        }

        private static void ScaleSkeletonLocalTranslations(Transform node, float factor)
        {
            if (node == null) return;
            node.localPosition *= factor;
            for (int i = 0; i < node.childCount; i++)
                ScaleSkeletonLocalTranslations(node.GetChild(i), factor);
        }
    }
}
