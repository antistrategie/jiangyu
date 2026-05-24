using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Editor utility that bakes a static-prop weapon addition prefab from a
    /// glTF source produced by scripts/bake_weapon.py.
    ///
    /// Equivalent to <see cref="BakeHumanoid"/> for handheld weapons: takes a
    /// GLB containing one mesh and attach-point empties under a shared root,
    /// clones the reference vanilla weapon's Menace/* shader material, slots
    /// the modder's textures into _BaseColorMap / _NormalMap / _MaskMap,
    /// applies the new material to the MeshRenderer, and writes the result
    /// as an addition prefab.
    ///
    /// Two ways to drive it:
    ///
    /// <list type="bullet">
    /// <item>Interactive: <c>Jiangyu → Bake weapon prefab from glTF…</c></item>
    /// <item>Batchmode: <c>-executeMethod Jiangyu.Mod.BakeWeapon.BakeBatch</c>
    ///   with <c>-gltfPath</c>, <c>-referencePrefab</c>, <c>-outputDir</c>,
    ///   <c>-outputName</c>, and optional <c>-textureBase</c>,
    ///   <c>-textureNormal</c>, <c>-textureMask</c>.</item>
    /// </list>
    /// </summary>
    internal sealed class BakeWeapon : EditorWindow
    {
        private GameObject _gltfAsset;
        private GameObject _referencePrefab;
        private Texture2D _textureBase;
        private Texture2D _textureNormal;
        private Texture2D _textureMask;
        private string _outputDir = "Assets/Prefabs";
        private string _outputName = "";

        [MenuItem("Jiangyu/Bake weapon prefab from glTF…")]
        private static void ShowWindow()
        {
            var w = GetWindow<BakeWeapon>("Bake Weapon");
            w.minSize = new Vector2(440, 320);
        }

        // Batchmode entry point. Invoke via:
        //   Unity -batchmode -nographics -quit -projectPath <unity/> \
        //         -executeMethod Jiangyu.Mod.BakeWeapon.BakeBatch \
        //         -gltfPath <Assets/Authored/.../raw.glb> \
        //         -referencePrefab <Assets/Imported/.../arc_assault_rifle_t1.prefab> \
        //         -outputDir <Assets/Prefabs> \
        //         -outputName <voymastina/ak15> \
        //         -textureBase <Assets/.../base.tga> \
        //         -textureNormal <Assets/.../normal.tga> \
        //         -textureMask <Assets/.../mask.png>   (optional)
        // Texture flags are optional. When omitted, the bake slots a 1x1
        // neutral default in place of any missing slot.
        public static void BakeBatch()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name, string fallback)
            {
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == name) return args[i + 1];
                return fallback;
            }
            var gltfPath = Arg("-gltfPath", null);
            var referencePrefabPath = Arg("-referencePrefab", null);
            var outputDir = Arg("-outputDir", "Assets/Prefabs");
            var outputName = Arg("-outputName", null);
            var textureBase = Arg("-textureBase", null);
            var textureNormal = Arg("-textureNormal", null);
            var textureMask = Arg("-textureMask", null);

            if (string.IsNullOrEmpty(gltfPath) || string.IsNullOrEmpty(referencePrefabPath) || string.IsNullOrEmpty(outputName))
            {
                Debug.LogError("Jiangyu BakeWeapon: -gltfPath, -referencePrefab, and -outputName are required.");
                EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.Refresh();
            var gltf = AssetDatabase.LoadAssetAtPath<GameObject>(gltfPath);
            var refPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(referencePrefabPath);
            if (gltf == null) { Debug.LogError("Jiangyu BakeWeapon: glTF not found at " + gltfPath); EditorApplication.Exit(1); return; }
            if (refPrefab == null) { Debug.LogError("Jiangyu BakeWeapon: reference prefab not found at " + referencePrefabPath); EditorApplication.Exit(1); return; }

            var window = ScriptableObject.CreateInstance<BakeWeapon>();
            try
            {
                window._gltfAsset = gltf;
                window._referencePrefab = refPrefab;
                window._outputDir = outputDir;
                window._outputName = outputName;
                window._textureBase = LoadTextureAsset(textureBase, sRGB: true, isNormal: false);
                window._textureNormal = LoadTextureAsset(textureNormal, sRGB: false, isNormal: true);
                window._textureMask = LoadTextureAsset(textureMask, sRGB: false, isNormal: false);
                window.Bake();
                Debug.Log("Jiangyu BakeWeapon: success.");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("Jiangyu BakeWeapon failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Bakes a weapon addition prefab from a glTF source.\n\n"
                + "Input glTF: produced by scripts/bake_weapon.py. Contains a "
                + "mesh + `muzzle` and `weapon_hand_l` empties under a shared root.\n\n"
                + "Reference prefab: an imported vanilla MENACE weapon (e.g. "
                + "from Assets/Imported/arc_assault_rifle_t1/). Provides the "
                + "Menace/* shader and material properties.\n\n"
                + "Textures: optional, in the OBJ/glTF V=0-bottom convention. "
                + "Missing texture slots fall back to 1x1 neutral defaults "
                + "(non-metallic, medium-rough) to dodge the chrome-blue "
                + "default-mask rendering bug.\n\n"
                + "Output: `<output dir>/<output name>/main.prefab` plus "
                + "`baked.mat`. KDL ref is `asset=\"<output name>/main\"`.",
                MessageType.Info);

            _gltfAsset = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Source glTF (.glb)",
                    "The GLB produced by scripts/bake_weapon.py, with the mesh and "
                    + "muzzle / weapon_hand_l empties already positioned."),
                _gltfAsset, typeof(GameObject), false);

            _referencePrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Reference prefab",
                    "An imported vanilla MENACE weapon (e.g. arc_assault_rifle_t1) "
                    + "whose material's Menace/* shader gets cloned onto the new prefab."),
                _referencePrefab, typeof(GameObject), false);

            EditorGUILayout.LabelField("Textures (optional)", EditorStyles.boldLabel);
            _textureBase = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Base map (_d)", "Diffuse / albedo. Imported as sRGB."),
                _textureBase, typeof(Texture2D), false);
            _textureNormal = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Normal map (_n)", "Tangent-space normals. Imported as Normal Map."),
                _textureNormal, typeof(Texture2D), false);
            _textureMask = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Mask map (HDRP)",
                    "HDRP MaskMap convention: R=Metallic, G=AO, B=Detail, A=Smoothness. "
                    + "Use the repacked output from `bake_weapon.py`, not a raw "
                    + "Sunborn _rmo file. Channel order differs and would render chrome blue."),
                _textureMask, typeof(Texture2D), false);

            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            _outputDir = EditorGUILayout.TextField(
                new GUIContent("Output dir", "Parent folder for the per-weapon subdir, relative to project root."),
                _outputDir);
            _outputName = EditorGUILayout.TextField(
                new GUIContent("Output name",
                    "Sub-path under `output dir`. Supports `/` (e.g. `voymastina/ak15`). "
                    + "KDL ref will be asset=\"<output name>/main\"."),
                _outputName);

            GUILayout.Space(10);
            using (new EditorGUI.DisabledScope(!CanBake()))
            {
                if (GUILayout.Button("Bake", GUILayout.Height(32)))
                {
                    try { Bake(); }
                    catch (Exception ex)
                    {
                        Debug.LogError("Jiangyu BakeWeapon failed: " + ex);
                        EditorUtility.DisplayDialog("Bake failed", ex.Message, "OK");
                    }
                }
            }
        }

        private bool CanBake() =>
            _gltfAsset != null
            && _referencePrefab != null
            && !string.IsNullOrWhiteSpace(_outputName)
            && !string.IsNullOrWhiteSpace(_outputDir);

        private void Bake()
        {
            var gltfPath = AssetDatabase.GetAssetPath(_gltfAsset);
            // Force a synchronous re-import of the glTF so its content
            // is on disk before LoadAssetAtPath. glTFast batched imports
            // otherwise race against editor pipeline state.
            AssetDatabase.ImportAsset(gltfPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var refRenderers = _referencePrefab.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            var referenceMaterial = refRenderers
                .Select(r => r.sharedMaterial)
                .FirstOrDefault(m => m != null);
            if (referenceMaterial == null)
                throw new InvalidOperationException("Reference prefab '" + _referencePrefab.name + "' has no MeshRenderer/material to sample.");
            Debug.Log("Jiangyu BakeWeapon: cloning material from reference shader '" + referenceMaterial.shader.name + "'.");

            var characterDir = (_outputDir.TrimEnd('/') + "/" + _outputName).Replace('\\', '/');
            Directory.CreateDirectory(characterDir);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(_gltfAsset);
            PrefabUtility.UnpackPrefabInstance(
                instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            try
            {
                instance.name = Path.GetFileName(_outputName);

                var newMaterial = BuildBakedMaterial(referenceMaterial, _textureBase, _textureNormal, _textureMask);
                var matPath = characterDir + "/baked.mat";
                AssetDatabase.CreateAsset(newMaterial, matPath);

                var renderers = instance.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                if (renderers.Length == 0)
                    throw new InvalidOperationException("glTF prefab '" + _gltfAsset.name + "' has no MeshRenderer. Weapon bake expects rigid meshes.");
                foreach (var renderer in renderers)
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                        slots[i] = newMaterial;
                    renderer.sharedMaterials = slots;
                }

                var prefabPath = characterDir + "/main.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);

                AssetDatabase.SaveAssets();
                Debug.Log("Jiangyu BakeWeapon: wrote " + prefabPath + " (material: " + matPath + ").");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static Material BuildBakedMaterial(
            Material reference, Texture2D baseColor, Texture2D normalMap, Texture2D maskMap)
        {
            var shader = reference.shader;
            var mat = new Material(shader)
            {
                name = "baked",
                enableInstancing = reference.enableInstancing,
                globalIlluminationFlags = reference.globalIlluminationFlags,
                renderQueue = reference.renderQueue,
            };
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
                        // Null all reference textures. They'd UV-map to the
                        // reference mesh, not ours. Slots below replace
                        // _BaseColorMap / _NormalMap / _MaskMap explicitly.
                        mat.SetTexture(name, null);
                        break;
                }
            }

            AssignTexture(mat, baseColor, new[] { "_BaseMap", "_BaseColorMap", "_MainTex" });

            // Fall back to 1x1 neutral defaults when the modder hasn't
            // supplied a mask/normal map, or supplied one in the wrong
            // channel convention. Sunborn's _rmo is Roughness/Metallic/AO,
            // HDRP's MaskMap expects Metallic/AO/Detail/Smoothness, and the
            // shader's "white" default reads as Metallic=1 which renders
            // chrome blue.
            var normalDefault = EnsureDefaultTexture(
                "Assets/Materials/Jiangyu/_jiangyu_flat_normal.png",
                new Color32(128, 128, 255, 255),
                isNormalMap: true);
            AssignTexture(mat, normalMap ?? normalDefault, new[] { "_NormalMap", "_BumpMap", "_Normal" });

            var maskDefault = EnsureDefaultTexture(
                "Assets/Materials/Jiangyu/_jiangyu_neutral_mask.png",
                new Color32(0, 255, 0, 128),
                isNormalMap: false);
            AssignTexture(mat, maskMap ?? maskDefault, new[] { "_MaskMap", "_Mask", "_MetallicGlossMap" });

            return mat;
        }

        private static void AssignTexture(Material mat, Texture2D texture, string[] candidateProps)
        {
            if (texture == null) return;
            foreach (var prop in candidateProps)
            {
                if (mat.HasProperty(prop))
                {
                    mat.SetTexture(prop, texture);
                    return;
                }
            }
            Debug.LogWarning("Jiangyu BakeWeapon: shader has no slot for " + texture.name + " in " + string.Join(", ", candidateProps));
        }

        private static Texture2D LoadTextureAsset(string path, bool sRGB, bool isNormal)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path))
            {
                foreach (var ext in new[] { ".png", ".tga", ".jpg", ".jpeg" })
                {
                    var candidate = path + ext;
                    if (File.Exists(candidate)) { path = candidate; break; }
                }
            }
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;
                if (importer.sRGBTexture != sRGB) { importer.sRGBTexture = sRGB; changed = true; }
                var targetType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (importer.textureType != targetType) { importer.textureType = targetType; changed = true; }
                if (changed) importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D EnsureDefaultTexture(string assetPath, Color32 colour, bool isNormalMap)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing != null) return existing;

            var dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false, linear: isNormalMap);
            tex.SetPixels32(new[] { colour });
            tex.Apply();
            File.WriteAllBytes(assetPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !isNormalMap;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }
    }
}
