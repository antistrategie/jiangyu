using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Editor utility for baking a humanoid addition prefab from a glTF
    /// source plus a vanilla MENACE soldier reference prefab.
    ///
    /// The glTF brings the new character's bones, mesh, and baked atlas
    /// texture. The reference prefab donates the Menace/* shader (which the
    /// Unity Editor renders magenta because it's an AssetRipper stub, but
    /// the loader rebinds at runtime) and the runtime AnimatorController.
    ///
    /// Output layout (for output name <c>MyCharacter</c>):
    /// <code>
    /// Assets/Prefabs/MyCharacter/
    /// ├── main.prefab
    /// ├── baked.mat
    /// └── avatar.asset
    /// </code>
    /// KDL reference: <c>asset="MyCharacter/main"</c>. The output name names
    /// the bundle namespace; <c>main</c> is the convention for the entry
    /// prefab within it.
    ///
    /// Requirements on the input glTF:
    ///  * Skeleton is in T-pose at rest. The avatar's muscle-zero is built
    ///    from the current bone transforms, so a non-T-pose rest pose will
    ///    produce a broken Mecanim retarget. Bake T-pose into the rest pose
    ///    in your DCC tool (Blender, Maya, etc.) before exporting.
    ///  * Bones use the MENACE humanoid naming convention (Hips, Spine,
    ///    Spine2, Neck, Head, Shoulder_L, UpperArm_L, LowerArm_L, Hand_L,
    ///    UpperLeg_L, LowerLeg_L, Foot_L, and R-side equivalents). Rename
    ///    in your DCC tool or asset pipeline before exporting if needed.
    ///  * LOD meshes are named <c>{basename}_LOD0</c> .. <c>{basename}_LODN</c>.
    ///    The basename is auto-detected from mesh names.
    /// </summary>
    internal sealed class BakeHumanoid : EditorWindow
    {
        private DefaultAsset _sourceFolder;
        private GameObject _referencePrefab;
        private string _outputName = "";
        private string _outputDir = "Assets/Prefabs";

        [MenuItem("Jiangyu/Bake humanoid prefab from glTF…")]
        private static void ShowWindow()
        {
            var w = GetWindow<BakeHumanoid>("Bake Humanoid");
            w.minSize = new Vector2(440, 260);
        }

        // Batchmode entry point. Invoke via:
        //   Unity -batchmode -nographics -quit -projectPath <unity/> \
        //         -executeMethod Jiangyu.Mod.BakeHumanoid.BakeBatch \
        //         -gltfFolder <Assets/Imported/MyCharacter> \
        //         -referencePrefab <Assets/Imported/.../soldier.prefab> \
        //         -outputDir <Assets/Prefabs> \
        //         -outputName <MyCharacter>
        // Requires the Unity Editor instance for this project to be closed
        // (Unity single-instances each project via Library/UnityLockfile).
        public static void BakeBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string name, string fallback)
            {
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == name) return args[i + 1];
                return fallback;
            }
            var gltfFolder = Arg("-gltfFolder", null);
            var referencePrefabPath = Arg("-referencePrefab", null);
            var outputDir = Arg("-outputDir", "Assets/Prefabs");
            var outputName = Arg("-outputName", null);

            if (string.IsNullOrEmpty(gltfFolder)
                || string.IsNullOrEmpty(referencePrefabPath)
                || string.IsNullOrEmpty(outputName))
            {
                Debug.LogError(
                    "Jiangyu BakeHumanoid: -gltfFolder, -referencePrefab, and -outputName are required.");
                EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.Refresh();
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(gltfFolder);
            var refPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(referencePrefabPath);
            if (folder == null)
            {
                Debug.LogError("Jiangyu BakeHumanoid: glTF folder not found: '" + gltfFolder + "'.");
                EditorApplication.Exit(1);
                return;
            }
            if (refPrefab == null)
            {
                Debug.LogError("Jiangyu BakeHumanoid: reference prefab not found: '" + referencePrefabPath + "'.");
                EditorApplication.Exit(1);
                return;
            }

            var window = ScriptableObject.CreateInstance<BakeHumanoid>();
            try
            {
                window._sourceFolder = folder;
                window._referencePrefab = refPrefab;
                window._outputDir = outputDir;
                window._outputName = outputName;
                window.Bake();
                Debug.Log("Jiangyu BakeHumanoid: success.");
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Jiangyu BakeHumanoid failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Bakes a humanoid addition prefab from a glTF source.\n\n"
                + "Requirements:\n"
                + " • The glTF skeleton must be in T-pose at rest. The avatar's "
                + "muscle-zero is built from the current bone transforms, so a "
                + "non-T-pose rest pose will produce a broken Mecanim retarget.\n"
                + " • Bones must use MENACE humanoid naming (Hips, Spine, Spine2, "
                + "Neck, Head, Shoulder_L, UpperArm_L, LowerArm_L, Hand_L, "
                + "UpperLeg_L, LowerLeg_L, Foot_L, and R-side equivalents).\n"
                + " • LOD meshes named `{basename}_LOD0..LODN` are picked up "
                + "automatically.\n\n"
                + "Output goes to `<output dir>/<output name>/` containing "
                + "`main.prefab`, `baked.mat`, and `avatar.asset`. The KDL "
                + "reference is `asset=\"<output name>/main\"`.",
                MessageType.Info);

            _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Source glTF folder",
                    "The folder containing model.gltf and the baked atlas texture (model_BaseColor.png)."),
                _sourceFolder, typeof(DefaultAsset), false);

            _referencePrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Reference prefab",
                    "An imported vanilla MENACE soldier prefab (e.g. one from Assets/Imported/...). "
                    + "Provides the Menace/* shader and AnimatorController."),
                _referencePrefab, typeof(GameObject), false);

            _outputDir = EditorGUILayout.TextField(
                new GUIContent("Output dir",
                    "Parent folder for the per-character subdir (relative to Assets/). "
                    + "The character's subdir is created inside this."),
                _outputDir);

            _outputName = EditorGUILayout.TextField(
                new GUIContent("Output name",
                    "Name of the per-character subdir (the bundle namespace). "
                    + "The prefab inside is always main.prefab. "
                    + "Used in KDL as asset=\"<output name>/main\"."),
                _outputName);

            GUILayout.Space(10);
            using (new EditorGUI.DisabledScope(!CanBake()))
            {
                if (GUILayout.Button("Bake", GUILayout.Height(32)))
                {
                    Bake();
                }
            }
        }

        private bool CanBake() =>
            _sourceFolder != null
            && _referencePrefab != null
            && !string.IsNullOrWhiteSpace(_outputName)
            && !string.IsNullOrWhiteSpace(_outputDir);

        private void Bake()
        {
            var sourceFolderPath = AssetDatabase.GetAssetPath(_sourceFolder);
            var gltfPath = Path.Combine(sourceFolderPath, "model.gltf").Replace('\\', '/');
            var atlasPath = Path.Combine(sourceFolderPath, "model_BaseColor.png").Replace('\\', '/');

            var gltfPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(gltfPath);
            if (gltfPrefab == null)
            {
                EditorUtility.DisplayDialog("Bake failed",
                    "Could not load glTF at " + gltfPath + ". Make sure the source folder contains model.gltf.",
                    "OK");
                return;
            }

            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
            if (atlas == null)
            {
                EditorUtility.DisplayDialog("Bake failed",
                    "Could not load atlas at " + atlasPath + ".",
                    "OK");
                return;
            }

            // Per-character subdir holds prefab + supporting artefacts.
            var characterDir = (_outputDir.TrimEnd('/') + "/" + _outputName).Replace('\\', '/');
            Directory.CreateDirectory(characterDir);

            // Sample one material from the reference for shader + non-texture
            // properties. Soldier reference prefabs typically use the same
            // Menace/* shader across all SkinnedMeshRenderers.
            var referenceInstance = (GameObject)PrefabUtility.InstantiatePrefab(_referencePrefab);
            try
            {
                var refSmrs = referenceInstance.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
                if (refSmrs.Length == 0)
                {
                    EditorUtility.DisplayDialog("Bake failed",
                        "Reference prefab has no SkinnedMeshRenderers; cannot extract shader.",
                        "OK");
                    return;
                }
                var referenceMaterial = refSmrs.Select(s => s.sharedMaterial).FirstOrDefault(m => m != null);
                if (referenceMaterial == null)
                {
                    EditorUtility.DisplayDialog("Bake failed",
                        "Reference prefab's SkinnedMeshRenderers have no shared material to sample shader from.",
                        "OK");
                    return;
                }

                var referenceAnimator = referenceInstance.GetComponentInChildren<Animator>(includeInactive: true);

                var bakedMaterial = BuildBakedMaterial(referenceMaterial, atlas);
                var materialPath = characterDir + "/baked.mat";
                AssetDatabase.CreateAsset(bakedMaterial, materialPath);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(gltfPrefab);
                // Unpack the prefab linkage so AddComponent on the root attaches
                // for real instead of being held as a prefab override that the
                // glTFast importer's read-only root may silently reject.
                PrefabUtility.UnpackPrefabInstance(
                    instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                try
                {
                    instance.name = _outputName;

                    // Match the reference rig's hierarchy by inserting a "Root"
                    // wrapper GameObject between the prefab root and Hips.
                    // The reference uses paths like "Root/Hips/Spine/..." in
                    // its avatar m_TOS; keeping the same shape lets the built
                    // avatar produce equivalent paths.
                    EnsureRootParentOverHips(instance);

                    // Build a per-character humanoid Avatar from the current
                    // (T-pose) scene state. Using a per-character avatar
                    // preserves the new character's own bone POSITIONS
                    // (proportions); reusing the reference Avatar directly
                    // would also import the reference's bone positions and
                    // distort the body when Mecanim retargets.
                    var avatar = BuildHumanoidAvatar(instance);
                    if (avatar == null || !avatar.isHuman)
                    {
                        EditorUtility.DisplayDialog("Bake failed",
                            "Could not build a humanoid Avatar from the imported skeleton. "
                            + "Check the Console for which bones are missing.",
                            "OK");
                        return;
                    }
                    var avatarPath = characterDir + "/avatar.asset";
                    AssetDatabase.CreateAsset(avatar, avatarPath);

                    AssignMaterialToAllSmrs(instance, bakedMaterial);
                    ConfigureAnimator(instance, avatar, referenceAnimator);
                    ConfigureLodGroup(instance);

                    // The prefab inside the per-character subdir is always
                    // named main.prefab. The subdir name carries the
                    // character identity (also Object.name on the prefab
                    // root); main is the entry-point convention so the KDL
                    // ref is asset="<subdir>/main" rather than the redundant
                    // "<subdir>/<subdir>".
                    var prefabPath = characterDir + "/main.prefab";
                    PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    Debug.Log("Jiangyu BakeHumanoid: wrote " + prefabPath
                        + " (material: " + materialPath + ", avatar: " + avatarPath + ").");
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
                }
                finally
                {
                    DestroyImmediate(instance);
                }
            }
            finally
            {
                DestroyImmediate(referenceInstance);
            }
        }

        private static Material BuildBakedMaterial(Material reference, Texture2D atlas)
        {
            var shader = reference.shader;
            var mat = new Material(shader)
            {
                name = "baked",
                enableInstancing = reference.enableInstancing,
                globalIlluminationFlags = reference.globalIlluminationFlags,
                renderQueue = reference.renderQueue,
            };

            // Copy keywords (e.g. _DISABLE_DECALS, _DISABLE_SSR) so the Menace
            // shader picks the same variant as the reference.
            mat.shaderKeywords = reference.shaderKeywords;

            var count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        mat.SetColor(name, reference.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        mat.SetFloat(name, reference.GetFloat(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        mat.SetVector(name, reference.GetVector(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        mat.SetInt(name, reference.GetInt(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        // Null every texture slot; we can't reuse the
                        // reference's utility maps (Normal / Mask / etc.)
                        // because they're UV-mapped to the REFERENCE mesh
                        // and would sample at wrong positions on the new
                        // mesh. Critical utility slots get 1x1 defaults
                        // assigned below.
                        mat.SetTexture(name, null);
                        break;
                }
            }

            // Assign the bake atlas to the most common base-map property names.
            foreach (var prop in BaseColorPropertyNames)
            {
                if (mat.HasProperty(prop))
                {
                    mat.SetTexture(prop, atlas);
                    break;
                }
            }

            // 1x1 defaults for utility-map slots so the shader doesn't fall
            // back to its built-in defaults (which can be "white" for mask
            // maps → Metallic=1 → chrome-blue render).
            var flatNormal = EnsureDefaultTexture(
                "Assets/Materials/Jiangyu/_jiangyu_flat_normal.png",
                new Color32(128, 128, 255, 255),
                isNormalMap: true);
            foreach (var prop in new[] { "_NormalMap", "_BumpMap", "_Normal" })
            {
                if (mat.HasProperty(prop))
                {
                    mat.SetTexture(prop, flatNormal);
                    break;
                }
            }

            var neutralMask = EnsureDefaultTexture(
                "Assets/Materials/Jiangyu/_jiangyu_neutral_mask.png",
                new Color32(0, 255, 0, 128),
                isNormalMap: false);
            foreach (var prop in new[] { "_MaskMap", "_Mask", "_MetallicGlossMap" })
            {
                if (mat.HasProperty(prop))
                {
                    mat.SetTexture(prop, neutralMask);
                    break;
                }
            }

            return mat;
        }

        // MENACE bone name → Unity humanoid muscle slot. The MENACE side
        // matches the vanilla soldier rig naming; bring your glTF skeleton
        // in with the same names (rename in your DCC tool or asset pipeline)
        // so this mapping resolves. Unity humanoid muscle names are
        // documented at https://docs.unity3d.com/Manual/HumanoidAvatar.html.
        private static readonly (string menace, string unityHumanoid)[] HumanoidBoneMapping =
        {
            ("Hips", "Hips"),
            ("Spine", "Spine"),
            ("Spine2", "Chest"),
            ("Neck", "Neck"),
            ("Head", "Head"),
            ("Shoulder_L", "LeftShoulder"),
            ("UpperArm_L", "LeftUpperArm"),
            ("LowerArm_L", "LeftLowerArm"),
            ("Hand_L", "LeftHand"),
            ("Shoulder_R", "RightShoulder"),
            ("UpperArm_R", "RightUpperArm"),
            ("LowerArm_R", "RightLowerArm"),
            ("Hand_R", "RightHand"),
            ("UpperLeg_L", "LeftUpperLeg"),
            ("LowerLeg_L", "LeftLowerLeg"),
            ("Foot_L", "LeftFoot"),
            ("UpperLeg_R", "RightUpperLeg"),
            ("LowerLeg_R", "RightLowerLeg"),
            ("Foot_R", "RightFoot"),
        };

        // Insert a "Root" GameObject between the prefab's root and the Hips
        // bone, matching the reference rig's hierarchy. Necessary so
        // Mecanim's path-based bone resolution (m_TOS in the avatar uses
        // "Root/Hips/...") can find the bones. Hips is reparented with
        // worldPositionStays so the visual position and the
        // SkinnedMeshRenderer.bones[] references are preserved.
        private static void EnsureRootParentOverHips(GameObject characterRoot)
        {
            var hips = characterRoot.transform.Find("Hips");
            if (hips == null)
            {
                Debug.LogWarning("Jiangyu BakeHumanoid: no 'Hips' child directly under prefab root; skipping Root wrapper insertion.");
                return;
            }
            if (hips.parent != characterRoot.transform)
            {
                Debug.Log("Jiangyu BakeHumanoid: Hips already has a non-root parent; assuming Root wrapper exists.");
                return;
            }

            var rootGo = new GameObject("Root");
            rootGo.transform.SetParent(characterRoot.transform, worldPositionStays: false);
            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.identity;
            rootGo.transform.localScale = Vector3.one;
            hips.SetParent(rootGo.transform, worldPositionStays: true);
            Debug.Log("Jiangyu BakeHumanoid: inserted 'Root' GameObject between prefab root and Hips.");
        }

        private static Avatar BuildHumanoidAvatar(GameObject root)
        {
            var allTransforms = root.GetComponentsInChildren<Transform>(includeInactive: true);

            var humanBones = new List<HumanBone>();
            var missing = new List<string>();
            foreach (var (menace, humanoid) in HumanoidBoneMapping)
            {
                if (allTransforms.Any(t => t.name == menace))
                {
                    humanBones.Add(new HumanBone
                    {
                        boneName = menace,
                        humanName = humanoid,
                        limit = new HumanLimit { useDefaultValues = true },
                    });
                }
                else
                {
                    missing.Add(menace);
                }
            }
            if (missing.Count > 0)
            {
                Debug.LogError(
                    "Jiangyu BakeHumanoid: cannot build humanoid Avatar; missing required bones "
                    + string.Join(", ", missing)
                    + ". Rename your skeleton to match these MENACE humanoid names before exporting.");
                return null;
            }

            var skeletonBones = new List<SkeletonBone>();
            foreach (var t in allTransforms)
            {
                skeletonBones.Add(new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });
            }

            var description = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeletonBones.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.name = root.name + "_avatar";
            if (!avatar.isValid)
            {
                Debug.LogError(
                    "Jiangyu BakeHumanoid: AvatarBuilder produced an invalid Avatar. Check the Console for Unity's specific complaints.");
                return null;
            }
            Debug.Log("Jiangyu BakeHumanoid: built humanoid Avatar with " + humanBones.Count + " mapped bones.");
            return avatar;
        }

        private static Texture2D EnsureDefaultTexture(string assetPath, Color32 color, bool isNormalMap)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing != null) return existing;

            var dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false, linear: isNormalMap);
            tex.SetPixel(0, 0, new Color(color.r / 255f, color.g / 255f, color.b / 255f, color.a / 255f));
            tex.Apply(updateMipmaps: false);
            File.WriteAllBytes(assetPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !isNormalMap;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Point;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // Common base-color property names across HDRP / URP / Built-in /
        // Menace shader variants.
        private static readonly string[] BaseColorPropertyNames =
        {
            "_BaseMap",
            "_BaseColorMap",
            "_MainTex",
            "_Albedo",
            "_AlbedoMap",
        };

        private static void AssignMaterialToAllSmrs(GameObject root, Material mat)
        {
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            foreach (var smr in smrs)
            {
                var slots = smr.sharedMaterials.Length;
                var arr = new Material[slots > 0 ? slots : 1];
                for (int i = 0; i < arr.Length; i++) arr[i] = mat;
                smr.sharedMaterials = arr;
            }
        }

        private static void ConfigureAnimator(GameObject root, Avatar avatar, Animator referenceAnimator)
        {
            // TryGetComponent + AddComponent pattern: Unity objects' "fake
            // null" semantics defeat the null-coalesce operator.
            if (!root.TryGetComponent<Animator>(out var anim))
                anim = root.AddComponent<Animator>();
            if (anim == null)
                throw new System.InvalidOperationException(
                    "Failed to attach Animator to '" + root.name + "'.");

            anim.avatar = avatar;
            anim.applyRootMotion = referenceAnimator?.applyRootMotion ?? false;
            anim.cullingMode = referenceAnimator?.cullingMode ?? AnimatorCullingMode.CullUpdateTransforms;
            if (referenceAnimator != null && referenceAnimator.runtimeAnimatorController != null)
                anim.runtimeAnimatorController = referenceAnimator.runtimeAnimatorController;
        }

        // Auto-detect LOD meshes: any SkinnedMeshRenderer with a sharedMesh
        // whose name matches "<basename>_LOD<N>" forms part of the chain.
        // Multiple basenames are an error (modder should run one character
        // at a time). The detected basename is logged for transparency.
        private static readonly Regex LodNameRegex = new Regex(@"^(?<basename>.+)_LOD(?<index>\d+)$", RegexOptions.Compiled);

        private static void ConfigureLodGroup(GameObject root)
        {
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            var perLod = new List<(int index, SkinnedMeshRenderer smr, string basename)>();
            foreach (var smr in smrs)
            {
                if (smr.sharedMesh == null) continue;
                var match = LodNameRegex.Match(smr.sharedMesh.name);
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups["index"].Value, out var lodIndex)) continue;
                perLod.Add((lodIndex, smr, match.Groups["basename"].Value));
            }

            if (perLod.Count == 0)
            {
                Debug.LogWarning(
                    "Jiangyu BakeHumanoid: no SkinnedMeshRenderers matched '<basename>_LOD<N>' naming. Skipping LODGroup.");
                return;
            }

            var distinctBasenames = perLod.Select(p => p.basename).Distinct().ToArray();
            if (distinctBasenames.Length > 1)
            {
                Debug.LogError(
                    "Jiangyu BakeHumanoid: multiple LOD basenames found ("
                    + string.Join(", ", distinctBasenames)
                    + "). Bake one character at a time.");
                return;
            }
            Debug.Log("Jiangyu BakeHumanoid: detected LOD basename '" + distinctBasenames[0] + "' (" + perLod.Count + " level(s)).");

            perLod.Sort((a, b) => a.index.CompareTo(b.index));

            if (!root.TryGetComponent<LODGroup>(out var lod))
                lod = root.AddComponent<LODGroup>();
            lod.fadeMode = LODFadeMode.CrossFade;
            lod.animateCrossFading = true;

            // Standard thresholds. Soldier-class reference prefabs use
            // around these screen-relative cutoffs.
            var thresholds = new float[] { 0.5f, 0.25f, 0.1f, 0.02f };
            var lods = new LOD[perLod.Count];
            for (int i = 0; i < perLod.Count; i++)
            {
                var t = i < thresholds.Length ? thresholds[i] : thresholds[thresholds.Length - 1] * 0.5f;
                lods[i] = new LOD(t, new Renderer[] { perLod[i].smr });
            }
            lod.SetLODs(lods);
            lod.RecalculateBounds();
        }
    }
}
