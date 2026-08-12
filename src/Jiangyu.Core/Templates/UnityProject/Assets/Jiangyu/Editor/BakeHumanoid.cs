using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using StringComparer = System.StringComparer;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Editor utility for baking a humanoid addition prefab from a glTF
    /// source plus a vanilla MENACE soldier reference prefab.
    ///
    /// The glTF brings the new character's bones, mesh, and source textures
    /// (one per glTF material). The reference prefab donates the Menace/*
    /// shader (which the Unity Editor renders magenta because it's an
    /// AssetRipper stub, but the loader rebinds at runtime) and the runtime
    /// AnimatorController.
    ///
    /// Output layout (for output name <c>MyCharacter</c>):
    /// <code>
    /// Assets/Prefabs/MyCharacter/
    /// ├── main.prefab
    /// ├── baked_&lt;source-material&gt;.mat   (one per unique BaseColor texture)
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
        private Shader _overrideShader;
        // Per source-material overrides, keyed by the glTF material name that
        // also names the baked asset. Takes precedence over _overrideShader.
        // A list rather than a dictionary so the window can show partly filled
        // rows while the modder is still typing.
        private readonly List<MaterialShaderOverride> _overrideShaderRows =
            new List<MaterialShaderOverride>();
        // Extra textures to set on a baked material, for maps a custom shader
        // declares beyond the base colour the bake already assigns. Flat rather
        // than nested per slot so one row reads as one assignment.
        private readonly List<MaterialTextureOverride> _textureRows =
            new List<MaterialTextureOverride>();
        private readonly List<MaterialFloatOverride> _floatRows =
            new List<MaterialFloatOverride>();
        private string _outputName = "";
        private string _outputDir = "Assets/Prefabs";
        private Vector2 _scroll;

        [MenuItem("Jiangyu/Bake humanoid prefab from glTF…")]
        private static void ShowWindow()
        {
            var w = GetWindow<BakeHumanoid>("Bake Humanoid");
            w.minSize = new Vector2(440, 260);
        }

        // Batchmode entry point. Invoke via:
        //   Unity -batchmode -nographics -quit -projectPath <unity/> \
        //         -executeMethod Jiangyu.Mod.BakeHumanoid.BakeBatch \
        //         -gltfFolder <Assets/Authored/MyCharacter> \
        //         -referencePrefab <Assets/Imported/.../soldier.prefab> \
        //         -outputDir <Assets/Prefabs> \
        //         -outputName <MyCharacter> \
        //         [-overrideShader <Womenace/DollToon>] \
        //         [-overrideShaderFor <Face=Womenace/DollFace,Hair=Womenace/DollHair>] \
        //         [-setTextureFor <Hair:_RampMap=Assets/.../ramp_hair.png,...>]
        //         [-setFloatFor <Face:_UseBlendTex=1,Hair:_AnisotropicSpecular=1>]
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
            var overrideShaderArg = Arg("-overrideShader", null);
            var overrideShaderForArg = Arg("-overrideShaderFor", null);
            var setTextureForArg = Arg("-setTextureFor", null);
            var setFloatForArg = Arg("-setFloatFor", null);

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
                window._overrideShader = ResolveOverrideShader(overrideShaderArg);
                ParseOverrideShaderFor(overrideShaderForArg, window._overrideShaderRows);
                ParseSetTextureFor(setTextureForArg, window._textureRows);
                ParseSetFloatFor(setFloatForArg, window._floatRows);
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
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

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
                    "The folder containing model.gltf and its source textures (one per glTF material)."),
                _sourceFolder, typeof(DefaultAsset), false);

            _referencePrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Reference prefab",
                    "An imported vanilla MENACE soldier prefab (e.g. one from Assets/Imported/...). "
                    + "Provides the Menace/* shader and AnimatorController."),
                _referencePrefab, typeof(GameObject), false);

            _overrideShader = (Shader)EditorGUILayout.ObjectField(
                new GUIContent("Override shader (optional)",
                    "A shader your mod ships. Leave empty to bake against the reference prefab's "
                    + "Menace/* shader, which the loader rebinds at runtime. Set it and the baked "
                    + "materials use your shader with its own authored defaults, and the loader "
                    + "keeps them on it. Source textures are assigned to whichever base-map, "
                    + "normal-map and mask-map property names your shader declares."),
                _overrideShader, typeof(Shader), false);

            DrawOverrideShaderRows();
            DrawTextureRows();
            DrawFloatRows();

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

            EditorGUILayout.EndScrollView();
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

            // Force a fresh synchronous reimport of the glTF. ForceUpdate
            // ensures Unity actually re-runs the importer (mtime check can
            // miss; LoadAssetAtPath may otherwise return null when the
            // cached import has not finished). ForceSynchronousImport runs
            // the import on the main thread, dodging glTFast's Jobs-system
            // race that surfaces in batchmode (SortAndNormalizeBoneWeightsJob
            // raced reading bones it was still writing).
            AssetDatabase.ImportAsset(gltfPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var gltfPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(gltfPath);
            if (gltfPrefab == null)
            {
                EditorUtility.DisplayDialog("Bake failed",
                    "Could not load glTF at " + gltfPath + ". Make sure the source folder contains model.gltf."
                    + SourceFolderHint(sourceFolderPath),
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

                    BakeMaterialsForSmrs(instance, referenceMaterial, characterDir, _overrideShader, BuildOverrideShaderMap(), _textureRows, _floatRows);
                    ConfigureAnimator(instance, avatar, referenceAnimator);
                    ConfigureLodGroup(instance);

                    // Copy the reference soldier's supplementary
                    // non-skeleton, non-LOD top-level children onto the
                    // baked prefab. The canonical case is the footstep
                    // dust spawn container (named `transform` on vanilla
                    // marines) holding `dust01` / `dust02` markers that
                    // MENACE's DustEffectsAnimatorComp walks for. Runtime
                    // can't add hierarchy children to a bundle asset
                    // after load (Unity treats asset prefabs as
                    // structurally immutable for live SetParent ops), so
                    // this has to happen at bake time, before the prefab
                    // is saved.
                    CopySupplementaryChildrenFromReference(instance, referenceInstance);

                    // Mirror the reference soldier's per-bone ragdoll physics
                    // (Rigidbody / Collider / CharacterJoint) onto matching
                    // bones in the baked rig. Without this, MENACE's
                    // MenaceRagdoll component has nothing to actuate on
                    // death and the unit stays standing.
                    MirrorRagdollPhysicsFromReference(instance, referenceInstance);

                    // Record the reference vanilla prefab's runtime Object.name
                    // on a sentinel child of the output prefab. The loader's
                    // humanoid mirror reads this at addition-load time to know
                    // which vanilla soldier to copy MonoBehaviour config from.
                    // Encoding via a child GameObject keeps the metadata
                    // script-free — Unity's bundle serialiser handles plain
                    // GameObject names natively, so no per-mod runtime
                    // assembly is needed to surface it.
                    var referenceName = _referencePrefab.name;
                    var sentinel = new GameObject("__jiangyu_ref:" + referenceName);
                    sentinel.transform.SetParent(instance.transform, worldPositionStays: false);
                    sentinel.SetActive(false);

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
                        + " (avatar: " + avatarPath + ").");
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

        // Bring over the reference soldier's top-level non-skeleton,
        // non-LOD children onto the baked prefab. Skeleton bones come
        // from the baked character's own avatar (humanoid retargeting
        // gives equivalent bones with the new character's proportions),
        // and LOD mesh containers are character-specific by definition.
        // What's left at the top level is supplementary scaffolding
        // MENACE's runtime walks for — footstep dust spawn containers
        // (`transform` holding `dust01` / `dust02`), audio source
        // markers, any other reference-shipped helpers. Filtered by
        // "contains a SkinnedMeshRenderer in its subtree" (LOD meshes)
        // and "is a humanoid bone or ancestor" (rig); everything else
        // gets DeepCopied wholesale onto the baked instance's root.
        // Names matching an existing child on the baked output are
        // skipped — the baked side already produced something
        // structurally equivalent (e.g. its own LODs).
        private static void CopySupplementaryChildrenFromReference(GameObject baked, GameObject reference)
        {
            var bakedTransform = baked.transform;
            var referenceTransform = reference.transform;
            var bones = CollectHumanoidBones(reference);

            var existing = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bakedTransform.childCount; i++)
                existing.Add(bakedTransform.GetChild(i).name);

            int copied = 0;
            for (int i = 0; i < referenceTransform.childCount; i++)
            {
                var child = referenceTransform.GetChild(i);
                if (child == null) continue;
                if (bones.Contains(child)) continue;
                if (existing.Contains(child.name)) continue;
                if (SubtreeContainsSkinnedMesh(child)) continue;
                // Imported vanilla prefabs carry __jiangyu_scripts markers on
                // script-bearing nodes. Those are for sub-assemblies a modder
                // copies deliberately; a humanoid bake mirrors its soldier
                // config through HumanoidPrefabMirror instead, so markers must
                // not ride the supplementary copy onto the baked character.
                if (child.name.StartsWith("__jiangyu_scripts", System.StringComparison.Ordinal)) continue;

                // Editor-side Instantiate-with-parent works on assets
                // because the baked GameObject lives in the editor
                // scene at this point in the bake (it was instantiated
                // off the glTF prefab earlier in this method). After
                // PrefabUtility.SaveAsPrefabAsset persists the
                // hierarchy, the saved prefab on disk includes the
                // copied subtree.
                var clone = (GameObject)UnityEngine.Object.Instantiate(child.gameObject, bakedTransform, worldPositionStays: false);
                clone.name = child.name;
                StripScriptMarkers(clone.transform);
                copied++;
            }

            if (copied > 0)
                Debug.Log(
                    $"Jiangyu BakeHumanoid: copied {copied} supplementary child subtree(s) from "
                    + $"'{reference.name}' onto baked prefab.");
        }

        private static void StripScriptMarkers(Transform subtree)
        {
            for (int i = subtree.childCount - 1; i >= 0; i--)
            {
                var child = subtree.GetChild(i);
                if (child == null) continue;
                if (child.name.StartsWith("__jiangyu_scripts", System.StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                else
                    StripScriptMarkers(child);
            }
        }

        // Copy the reference soldier's per-bone Rigidbody / Collider /
        // CharacterJoint onto matching bones in the baked rig. Bone
        // matching is by Object.name, since humanoid bones share the
        // same names across vanilla and baked rigs by convention.
        // CharacterJoint.connectedBody references are remapped from
        // the reference's Rigidbody pointers to the baked rig's
        // freshly-pasted equivalents.
        //
        // Three passes preserve ordering: Rigidbody first (joints
        // reference it via connectedBody), Colliders second, Joints
        // last.
        private static void MirrorRagdollPhysicsFromReference(GameObject baked, GameObject reference)
        {
            var bakedBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var t in baked.GetComponentsInChildren<Transform>(includeInactive: true))
                bakedBones[t.name] = t;

            // Pass 1: Rigidbody. Record ref→baked mapping so joint
            // connectedBody references can be remapped in Pass 3.
            var rigidbodyMap = new Dictionary<Rigidbody, Rigidbody>();
            foreach (var refRb in reference.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                var boneName = refRb.gameObject.name;
                if (!bakedBones.TryGetValue(boneName, out var targetTransform))
                {
                    Debug.LogWarning(
                        $"Jiangyu BakeHumanoid: ragdoll bone '{boneName}' not found in baked rig; "
                        + "skipping Rigidbody.");
                    continue;
                }
                ComponentUtility.CopyComponent(refRb);
                ComponentUtility.PasteComponentAsNew(targetTransform.gameObject);
                rigidbodyMap[refRb] = targetTransform.gameObject.GetComponent<Rigidbody>();
            }

            // Pass 2: Colliders. A bone may carry more than one collider
            // (the vanilla rig has Spine2 with both BoxCollider and
            // SphereCollider). PasteComponentAsNew appends rather than
            // replacing, so iterating per-collider-instance covers that.
            foreach (var refCollider in reference.GetComponentsInChildren<Collider>(includeInactive: true))
            {
                var boneName = refCollider.gameObject.name;
                if (!bakedBones.TryGetValue(boneName, out var targetTransform))
                    continue;
                ComponentUtility.CopyComponent(refCollider);
                ComponentUtility.PasteComponentAsNew(targetTransform.gameObject);
            }

            // Pass 3: CharacterJoint, with connectedBody remap from
            // reference Rigidbody → baked Rigidbody via the map built
            // in Pass 1. Joints whose connectedBody isn't in the map
            // (cross-prefab edge case) leave the field null and log.
            foreach (var refJoint in reference.GetComponentsInChildren<CharacterJoint>(includeInactive: true))
            {
                var boneName = refJoint.gameObject.name;
                if (!bakedBones.TryGetValue(boneName, out var targetTransform))
                    continue;
                ComponentUtility.CopyComponent(refJoint);
                ComponentUtility.PasteComponentAsNew(targetTransform.gameObject);
                var newJoint = targetTransform.gameObject.GetComponent<CharacterJoint>();
                if (refJoint.connectedBody != null
                    && rigidbodyMap.TryGetValue(refJoint.connectedBody, out var remapped))
                {
                    newJoint.connectedBody = remapped;
                }
                else if (refJoint.connectedBody != null)
                {
                    Debug.LogWarning(
                        $"Jiangyu BakeHumanoid: CharacterJoint on '{boneName}' references Rigidbody on "
                        + $"'{refJoint.connectedBody.gameObject.name}' which has no baked equivalent. "
                        + "Joint connectedBody left null.");
                    newJoint.connectedBody = null;
                }
            }
        }

        private static HashSet<Transform> CollectHumanoidBones(GameObject root)
        {
            var bones = new HashSet<Transform>();
            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                return bones;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)i);
                if (bone == null) continue;
                var current = bone;
                while (current != null && current != root.transform)
                {
                    bones.Add(current);
                    current = current.parent;
                }
            }
            return bones;
        }

        private static bool SubtreeContainsSkinnedMesh(Transform node)
        {
            return node.GetComponentInChildren<SkinnedMeshRenderer>(includeInactive: true) != null;
        }

        // Resolve an override shader from a batch argument. Accepts an asset
        // path (Assets/Shaders/DollToon.shader) or a shader name
        // (Womenace/DollToon). An absent argument returns null, which bakes
        // against the reference material as usual. An argument that resolves to
        // nothing throws rather than falling back, so a typo cannot quietly
        // ship a doll on the vanilla shader.
        private static Shader ResolveOverrideShader(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;
            var byPath = AssetDatabase.LoadAssetAtPath<Shader>(arg);
            if (byPath != null) return byPath;
            var byName = Shader.Find(arg);
            if (byName != null) return byName;
            throw new System.InvalidOperationException(
                "Jiangyu BakeHumanoid: override shader not found: '" + arg
                + "'. Give a shader asset path or a shader name.");
        }

        // Parse a comma-separated per-material override list of the form
        // "Face=Womenace/DollFace,Hair=Womenace/DollHair". The key is the glTF
        // source material name, which is also the name the baked asset takes.
        private static void ParseOverrideShaderFor(string arg, List<MaterialShaderOverride> into)
        {
            if (string.IsNullOrEmpty(arg)) return;
            foreach (var pair in arg.Split(','))
            {
                if (pair.Length == 0) continue;
                var split = pair.IndexOf('=');
                if (split <= 0)
                    throw new System.InvalidOperationException(
                        "Jiangyu BakeHumanoid: -overrideShaderFor entry '" + pair
                        + "' is not of the form <sourceMaterial>=<shader>.");
                var key = pair.Substring(0, split).Trim();
                var value = pair.Substring(split + 1).Trim();
                into.Add(new MaterialShaderOverride { sourceMaterial = key, shader = ResolveOverrideShader(value) });
            }
        }

        // Parse a comma-separated list of the form
        // "Hair:_RampMap=Assets/.../ramp_hair.png,Face:_SdfMap=Assets/.../sdf.png".
        // The key before the colon is the glTF source material name and the one
        // after it is the shader property to set.
        private static void ParseSetTextureFor(string arg, List<MaterialTextureOverride> into)
        {
            if (string.IsNullOrEmpty(arg)) return;
            foreach (var entry in arg.Split(','))
            {
                if (entry.Length == 0) continue;
                var eq = entry.IndexOf('=');
                var colon = entry.IndexOf(':');
                if (eq <= 0 || colon <= 0 || colon > eq)
                    throw new System.InvalidOperationException(
                        "Jiangyu BakeHumanoid: -setTextureFor entry '" + entry
                        + "' is not of the form <sourceMaterial>:<property>=<texture asset path>.");

                var slot = entry.Substring(0, colon).Trim();
                var property = entry.Substring(colon + 1, eq - colon - 1).Trim();
                var path = entry.Substring(eq + 1).Trim();
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
                if (texture == null)
                    throw new System.InvalidOperationException(
                        "Jiangyu BakeHumanoid: -setTextureFor texture not found: '" + path + "'.");
                into.Add(new MaterialTextureOverride
                {
                    sourceMaterial = slot,
                    propertyName = property,
                    texture = texture,
                });
            }
        }

        // Parse a comma-separated list of the form
        // "Face:_UseBlendTex=1,Hair:_AnisotropicSpecular=1". The key before the
        // colon is the glTF source material name and the one after it is the
        // shader property to set.
        //
        // A shader cannot read a texture's filename or tell a keyword from its
        // absence, so a feature the material has to declare arrives as a float:
        // whether an RMO map holds roughness or smoothness, whether a material
        // takes the hair path, whether a face runs its SDF shading. Without this,
        // those either sit at a shader-wide default or get inferred from a
        // sentinel value packed into a texture.
        private static void ParseSetFloatFor(string arg, List<MaterialFloatOverride> into)
        {
            if (string.IsNullOrEmpty(arg)) return;
            foreach (var entry in arg.Split(','))
            {
                if (entry.Length == 0) continue;
                var eq = entry.IndexOf('=');
                var colon = entry.IndexOf(':');
                if (eq <= 0 || colon <= 0 || colon > eq)
                    throw new System.InvalidOperationException(
                        "Jiangyu BakeHumanoid: -setFloatFor entry '" + entry
                        + "' is not of the form <sourceMaterial>:<property>=<number>.");

                var slot = entry.Substring(0, colon).Trim();
                var property = entry.Substring(colon + 1, eq - colon - 1).Trim();
                var raw = entry.Substring(eq + 1).Trim();
                if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new System.InvalidOperationException(
                        "Jiangyu BakeHumanoid: -setFloatFor value '" + raw + "' is not a number.");
                into.Add(new MaterialFloatOverride
                {
                    sourceMaterial = slot,
                    propertyName = property,
                    value = value,
                });
            }
        }

        private static Material BuildBakedMaterial(Material reference, Texture2D baseColor, Shader overrideShader)
        {
            // An override shader keeps its own authored defaults. The reference
            // material's property values and keywords belong to the Menace
            // shader, and copying them across would zero the override's
            // defaults wherever the two disagree on a property name.
            var shader = overrideShader != null ? overrideShader : reference.shader;
            var mat = new Material(shader) { name = "baked" };
            if (overrideShader == null)
            {
                mat.enableInstancing = reference.enableInstancing;
                mat.globalIlluminationFlags = reference.globalIlluminationFlags;
                mat.renderQueue = reference.renderQueue;

                // Copy keywords (e.g. _DISABLE_DECALS, _DISABLE_SSR) so the Menace
                // shader picks the same variant as the reference.
                mat.shaderKeywords = reference.shaderKeywords;
            }

            var count = overrideShader != null ? 0 : shader.GetPropertyCount();
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

            // Assign the source texture to the most common base-map property
            // names. Null is fine: leaves the shader's default white texture.
            if (baseColor != null)
            {
                foreach (var prop in BaseColorPropertyNames)
                {
                    if (mat.HasProperty(prop))
                    {
                        mat.SetTexture(prop, baseColor);
                        break;
                    }
                }
            }

            // 1x1 defaults for utility-map slots so the shader doesn't fall
            // back to its built-in defaults (which can be "white" for mask
            // maps → Metallic=1 → chrome-blue render).
            var flatNormal = EnsureDefaultTexture(
                "Assets/Materials/Jiangyu/_jiangyu_flat_normal.png",
                new Color32(128, 128, 255, 255),
                isNormalMap: true, linear: true);
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
                isNormalMap: false, linear: true);
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

        private static Texture2D EnsureDefaultTexture(string assetPath, Color32 colour, bool isNormalMap, bool linear)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing == null)
            {
                var dir = Path.GetDirectoryName(assetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false, linear: linear);
                tex.SetPixel(0, 0, new Color(colour.r / 255f, colour.g / 255f, colour.b / 255f, colour.a / 255f));
                tex.Apply(updateMipmaps: false);
                File.WriteAllBytes(assetPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            }

            // Enforce the requested import settings even on a pre-existing
            // asset: sibling bake tools share default-texture paths, and the
            // first tool to run must not lock in conflicting settings.
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                var wantType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                var wantSrgb = !isNormalMap && !linear;
                if (importer.textureType != wantType || importer.sRGBTexture != wantSrgb || importer.mipmapEnabled)
                {
                    importer.textureType = wantType;
                    importer.sRGBTexture = wantSrgb;
                    importer.mipmapEnabled = false;
                    importer.filterMode = FilterMode.Point;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.SaveAndReimport();
                }
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

        // For each submesh on every SkinnedMeshRenderer, build a baked
        // material that uses the reference soldier's shader (so the runtime
        // resolves to MENACE's vanilla shader at the same GUID) with the
        // per-submesh BaseColor texture taken from the gltf's auto-imported
        // material. Materials with the same BaseColor texture share one
        // baked asset (dedupe by texture). Works for both single-texture
        // glTFs (one baked material out) and multi-texture glTFs (one per
        // unique source texture).
        // The source folder is the one directly holding model.gltf. A character
        // with several outfits keeps one glTF per outfit subfolder, so the
        // character folder itself holds none and is the easy wrong pick. Name
        // the subfolders that do hold one so the right choice is visible.
        private static string SourceFolderHint(string sourceFolderPath)
        {
            if (string.IsNullOrEmpty(sourceFolderPath) || !Directory.Exists(sourceFolderPath))
                return "";

            // Assets/Prefabs/ is where the bake writes its output, so a folder
            // picked from there is the result of a previous bake rather than a
            // source. The source folder mirrors the same relative path under
            // Assets/Authored/, so name it directly when it exists.
            const string outputPrefix = "Assets/Prefabs/";
            var normalised = sourceFolderPath.Replace('\\', '/');
            if (normalised.StartsWith(outputPrefix, System.StringComparison.Ordinal))
            {
                var mirrored = "Assets/Authored/" + normalised.Substring(outputPrefix.Length);
                var mirroredHasGltf = File.Exists(Path.Combine(mirrored, "model.gltf"));
                return " Assets/Prefabs/ holds bake output, not sources."
                    + (mirroredHasGltf
                        ? " The source for this one is '" + mirrored + "'."
                        : " Sources live under Assets/Authored/.");
            }

            var candidates = Directory.GetDirectories(sourceFolderPath)
                .Where(d => File.Exists(Path.Combine(d, "model.gltf")))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
                return "";
            return " Subfolders that do hold one: " + string.Join(", ", candidates)
                + ". Pick one of those instead.";
        }

        [System.Serializable]
        private class MaterialShaderOverride
        {
            public string sourceMaterial = "";
            public Shader shader;
        }

        [System.Serializable]
        private class MaterialTextureOverride
        {
            public string sourceMaterial = "";
            public string propertyName = "";
            public Texture texture;
        }

        [System.Serializable]
        private class MaterialFloatOverride
        {
            public string sourceMaterial = "";
            public string propertyName = "";
            public float value;
        }

        // Per-material overrides in the window. The same rows the
        // -overrideShaderFor batch argument fills, so neither entry point is
        // more capable than the other.
        private void DrawOverrideShaderRows()
        {
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Per-material shader overrides (optional)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Give one material its own shader, keyed by the glTF material name (the name the "
                + "baked asset takes). Takes precedence over the blanket override shader above. "
                + "Use it where one slot needs different treatment, such as keeping an outline "
                + "pass off a face or eyes.\n\n"
                + "\"Fill from source glTF\" lists the material names the source actually has, so "
                + "the keys do not have to be typed from memory.",
                MessageType.None);

            var removeAt = -1;
            for (int i = 0; i < _overrideShaderRows.Count; i++)
            {
                var row = _overrideShaderRows[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.sourceMaterial = EditorGUILayout.TextField(row.sourceMaterial);
                    row.shader = (Shader)EditorGUILayout.ObjectField(row.shader, typeof(Shader), false);
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                        removeAt = i;
                }
            }
            if (removeAt >= 0)
                _overrideShaderRows.RemoveAt(removeAt);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add override"))
                    _overrideShaderRows.Add(new MaterialShaderOverride());
                using (new EditorGUI.DisabledScope(_sourceFolder == null))
                {
                    if (GUILayout.Button("Fill from source glTF"))
                        FillOverrideShaderRowsFromSource();
                }
            }
        }

        // Extra texture assignments in the window, the same rows the
        // -setTextureFor batch argument fills.
        private void DrawTextureRows()
        {
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Extra material textures (optional)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Set a texture on a baked material beyond the base colour the bake assigns, for "
                + "maps a custom shader declares such as a ramp atlas or a face threshold map. "
                + "Name the glTF source material, the shader property, and the texture. Source "
                + "materials that share a base colour texture bake into one material, so naming "
                + "any one of them assigns to the whole group.",
                MessageType.None);

            var removeAt = -1;
            for (int i = 0; i < _textureRows.Count; i++)
            {
                var row = _textureRows[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.sourceMaterial = EditorGUILayout.TextField(row.sourceMaterial);
                    row.propertyName = EditorGUILayout.TextField(row.propertyName);
                    row.texture = (Texture)EditorGUILayout.ObjectField(
                        row.texture, typeof(Texture), false);
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                        removeAt = i;
                }
            }
            if (removeAt >= 0)
                _textureRows.RemoveAt(removeAt);

            if (GUILayout.Button("Add texture"))
                _textureRows.Add(new MaterialTextureOverride());
        }

        // Per-material float assignments in the window, the same rows the
        // -setFloatFor batch argument fills.
        private void DrawFloatRows()
        {
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Material flags and values (optional)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Set a float on a baked material. This is how a material declares a feature its "
                + "shader cannot detect for itself: a shader cannot read a texture's filename or "
                + "tell a keyword from its absence, so whether an RMO map holds roughness or "
                + "smoothness, whether a material takes the hair path, or whether a face runs its "
                + "SDF shading all have to arrive as a number.\n\n"
                + "Name the glTF source material, the shader property, and the value. Source "
                + "materials that share a base colour texture bake into one material, so naming "
                + "any one of them assigns to the whole group.",
                MessageType.None);

            var removeAt = -1;
            for (int i = 0; i < _floatRows.Count; i++)
            {
                var row = _floatRows[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.sourceMaterial = EditorGUILayout.TextField(row.sourceMaterial);
                    row.propertyName = EditorGUILayout.TextField(row.propertyName);
                    row.value = EditorGUILayout.FloatField(row.value, GUILayout.Width(70));
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                        removeAt = i;
                }
            }
            if (removeAt >= 0)
                _floatRows.RemoveAt(removeAt);

            if (GUILayout.Button("Add value"))
                _floatRows.Add(new MaterialFloatOverride());
        }

        // Add a row per material name the source glTF carries, leaving the
        // shader empty. Existing rows are kept, so this is safe to press twice.
        private void FillOverrideShaderRowsFromSource()
        {
            var sourceFolderPath = AssetDatabase.GetAssetPath(_sourceFolder);
            var gltfPath = Path.Combine(sourceFolderPath, "model.gltf").Replace('\\', '/');
            GameObject gltfPrefab = null;
            if (File.Exists(gltfPath))
            {
                AssetDatabase.ImportAsset(gltfPath, ImportAssetOptions.ForceSynchronousImport);
                gltfPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(gltfPath);
            }
            if (gltfPrefab == null)
            {
                Debug.LogError(
                    "Jiangyu BakeHumanoid: no model.gltf to read directly inside '" + sourceFolderPath
                    + "'." + SourceFolderHint(sourceFolderPath));
                return;
            }

            var names = gltfPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true)
                .SelectMany(smr => smr.sharedMaterials)
                .Where(m => m != null && !string.IsNullOrEmpty(m.name))
                .Select(m => m.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var name in names)
            {
                var already = _overrideShaderRows.Any(r => string.Equals(
                    r.sourceMaterial, name, System.StringComparison.OrdinalIgnoreCase));
                if (already) continue;
                _overrideShaderRows.Add(new MaterialShaderOverride { sourceMaterial = name });
                added++;
            }
            Debug.Log("Jiangyu BakeHumanoid: added " + added + " material row(s) from " + gltfPath + ".");
        }

        // Project the rows into a lookup. Rows missing either half are skipped:
        // in the window they are a row the modder has not finished filling in,
        // and the batch path already threw on an unresolvable shader name.
        private Dictionary<string, Shader> BuildOverrideShaderMap()
        {
            var map = new Dictionary<string, Shader>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _overrideShaderRows)
            {
                if (row == null || row.shader == null) continue;
                if (string.IsNullOrWhiteSpace(row.sourceMaterial)) continue;
                map[row.sourceMaterial.Trim()] = row.shader;
            }
            return map;
        }

        // Match an override key against a source material name. Three forms are
        // accepted so a modder can write the name they see: the raw glTF name,
        // the sanitised form the baked asset is named after, and the name with a
        // trailing duplicate suffix removed. That suffix (Hair.001, Teeth.001)
        // is an exporter artefact for a repeated name, not a distinct part, so
        // "Hair" is taken to mean Hair and Hair.001 both.
        //
        // How specifically the key names this material: 3 for the material's
        // own name, 2 for the sanitised form, 1 for the suffix-stripped form,
        // and 0 for no match. A key that names the material exactly outranks
        // one that reached it by having its duplicate suffix stripped, so
        // "Hair.001" can be given its own treatment while "Hair" still covers
        // the pair by default.
        private static int MatchPrecision(string key, Material srcMat)
        {
            if (srcMat == null || string.IsNullOrWhiteSpace(key)) return 0;
            var k = key.Trim();
            var name = srcMat.name ?? "";
            if (string.Equals(k, name, System.StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(k, SanitiseAssetName(name), System.StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(k, StripDuplicateSuffix(name), System.StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        private static string StripDuplicateSuffix(string name)
            => string.IsNullOrEmpty(name) ? name : Regex.Replace(name, @"\.\d+$", "");

        // A source material's own entry wins, then the blanket override, then
        // null, which leaves the reference material's shader in place.
        private static Shader ResolveShaderForSource(
            Material srcMat, Shader blanket, Dictionary<string, Shader> bySource)
        {
            if (srcMat != null && bySource != null)
            {
                Shader best = null;
                var bestPrecision = 0;
                foreach (var pair in bySource)
                {
                    var precision = MatchPrecision(pair.Key, srcMat);
                    if (precision <= bestPrecision) continue;
                    best = pair.Value;
                    bestPrecision = precision;
                }
                if (best != null) return best;
            }
            return blanket;
        }

        // One assignment per shader property, taking the most specifically
        // named row. Without collapsing per property, a material reachable by
        // two equivalent keys would carry the assignment twice and so bake apart
        // from its own duplicate, which is the split this is meant to avoid.
        private static List<MaterialTextureOverride> ResolveExtrasForSource(
            Material srcMat, List<MaterialTextureOverride> textureRows)
        {
            var result = new List<MaterialTextureOverride>();
            if (srcMat == null || textureRows == null) return result;

            var bestByProperty = new Dictionary<string, MaterialTextureOverride>(StringComparer.OrdinalIgnoreCase);
            var precisionByProperty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in textureRows)
            {
                if (row == null || row.texture == null) continue;
                if (string.IsNullOrWhiteSpace(row.propertyName)) continue;
                var precision = MatchPrecision(row.sourceMaterial, srcMat);
                if (precision == 0) continue;
                var property = row.propertyName.Trim();
                if (precisionByProperty.TryGetValue(property, out var already) && already >= precision)
                    continue;
                bestByProperty[property] = row;
                precisionByProperty[property] = precision;
            }
            foreach (var property in bestByProperty.Keys.OrderBy(k => k, StringComparer.Ordinal))
                result.Add(bestByProperty[property]);
            return result;
        }

        // The float equivalent of ResolveExtrasForSource, collapsed per property
        // by the same precision rule so a material reachable by two equivalent
        // rows does not bake apart from its own duplicate.
        private static List<MaterialFloatOverride> ResolveFloatsForSource(
            Material srcMat, List<MaterialFloatOverride> floatRows)
        {
            var result = new List<MaterialFloatOverride>();
            if (srcMat == null || floatRows == null) return result;

            var bestByProperty = new Dictionary<string, MaterialFloatOverride>(StringComparer.OrdinalIgnoreCase);
            var precisionByProperty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in floatRows)
            {
                if (row == null) continue;
                if (string.IsNullOrWhiteSpace(row.propertyName)) continue;
                var precision = MatchPrecision(row.sourceMaterial, srcMat);
                if (precision == 0) continue;
                var property = row.propertyName.Trim();
                if (precisionByProperty.TryGetValue(property, out var already) && already >= precision)
                    continue;
                bestByProperty[property] = row;
                precisionByProperty[property] = precision;
            }
            foreach (var property in bestByProperty.Keys.OrderBy(k => k, StringComparer.Ordinal))
                result.Add(bestByProperty[property]);
            return result;
        }

        // Everything that makes a baked material distinct, so two source
        // materials merge only when the result would be identical. Sharing a
        // BaseColor texture is not enough on its own: a body texture can carry
        // bare skin, a cloth layer and stockings, and those take different
        // shaders and different ramp atlases, so they have to bake apart.
        private static string BakeKeyFor(
            Texture textureKey, Shader shader, List<MaterialTextureOverride> extras,
            List<MaterialFloatOverride> floats)
        {
            var key = (textureKey != null ? textureKey.GetInstanceID() : 0).ToString()
                + "|" + (shader != null ? shader.GetInstanceID() : 0).ToString();
            if (extras != null && extras.Count > 0)
            {
                foreach (var e in extras
                    .OrderBy(x => x.propertyName, StringComparer.Ordinal)
                    .ThenBy(x => x.texture != null ? x.texture.GetInstanceID() : 0))
                {
                    key += "|" + e.propertyName + "="
                        + (e.texture != null ? e.texture.GetInstanceID() : 0).ToString();
                }
            }
            if (floats != null && floats.Count > 0)
            {
                foreach (var f in floats.OrderBy(x => x.propertyName, StringComparer.Ordinal))
                {
                    key += "|" + f.propertyName + "="
                        + f.value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            return key;
        }

        private static void BakeMaterialsForSmrs(
            GameObject root, Material referenceMaterial, string characterDir,
            Shader overrideShader, Dictionary<string, Shader> overrideShaderBySource,
            List<MaterialTextureOverride> textureRows,
            List<MaterialFloatOverride> floatRows)
        {
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            // Captured before the bake loop swaps the baked materials in, so
            // the unmatched-key check at the end still sees the source names.
            var sourceMaterials = smrs
                .SelectMany(smr => smr.sharedMaterials ?? new Material[0])
                .Where(m => m != null)
                .Distinct()
                .ToArray();

            // Purge the previous run's output before generating. A renamed source
            // material, or one that now shares a baked material with another,
            // otherwise leaves an orphan .mat beside the live set, and a
            // committed orphan is where a machine-local stub-shader GUID goes
            // dangling: the magenta-model failure.
            foreach (var stale in Directory.GetFiles(characterDir, "baked*.mat"))
                AssetDatabase.DeleteAsset(stale.Replace('\\', '/'));

            var bakedByKey = new Dictionary<string, Material>(StringComparer.Ordinal);
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Which baked materials each texture produced, so a texture that
            // bakes into more than one can say so rather than surprise anyone.
            var namesByTexture = new Dictionary<Texture, List<string>>();

            foreach (var smr in smrs)
            {
                var source = smr.sharedMaterials;
                if (source == null || source.Length == 0)
                    continue;

                var baked = new Material[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    var srcMat = source[i];
                    var srcTexture = ExtractBaseColorTexture(srcMat);
                    var textureKey = TextureKeyFor(srcMat);
                    var shaderForSlot = ResolveShaderForSource(srcMat, overrideShader, overrideShaderBySource);
                    var extras = ResolveExtrasForSource(srcMat, textureRows);
                    var floats = ResolveFloatsForSource(srcMat, floatRows);
                    var key = BakeKeyFor(textureKey, shaderForSlot, extras, floats);

                    if (!bakedByKey.TryGetValue(key, out var bakedMat))
                    {
                        bakedMat = BuildBakedMaterial(referenceMaterial, srcTexture, shaderForSlot);
                        foreach (var row in extras)
                        {
                            // A property the shader does not declare is a typo
                            // worth surfacing: SetTexture would accept it
                            // silently and the map would never bind.
                            if (!bakedMat.HasProperty(row.propertyName))
                            {
                                Debug.LogWarning(
                                    "Jiangyu BakeHumanoid: shader '" + bakedMat.shader.name
                                    + "' has no texture property '" + row.propertyName
                                    + "', so the assignment for source material '"
                                    + row.sourceMaterial + "' is skipped.");
                                continue;
                            }
                            bakedMat.SetTexture(row.propertyName, row.texture);
                        }
                        foreach (var row in floats)
                        {
                            if (!bakedMat.HasProperty(row.propertyName))
                            {
                                Debug.LogWarning(
                                    "Jiangyu BakeHumanoid: shader '" + bakedMat.shader.name
                                    + "' has no float property '" + row.propertyName
                                    + "', so the assignment for source material '"
                                    + row.sourceMaterial + "' is skipped.");
                                continue;
                            }
                            bakedMat.SetFloat(row.propertyName, row.value);
                        }

                        var stem = (srcMat != null && !string.IsNullOrEmpty(srcMat.name))
                            ? "baked_" + SanitiseAssetName(srcMat.name)
                            : "baked";
                        var matName = stem;
                        for (int n = 2; !usedFileNames.Add(matName); n++)
                            matName = stem + "_" + n;
                        bakedMat.name = matName;
                        AssetDatabase.CreateAsset(bakedMat, characterDir + "/" + matName + ".mat");
                        bakedByKey[key] = bakedMat;

                        if (!namesByTexture.TryGetValue(textureKey, out var list))
                            namesByTexture[textureKey] = list = new List<string>();
                        list.Add(matName);
                    }
                    baked[i] = bakedMat;
                }
                smr.sharedMaterials = baked;
            }

            foreach (var pair in namesByTexture)
            {
                if (pair.Value.Count < 2) continue;
                Debug.Log(
                    "Jiangyu BakeHumanoid: one BaseColor texture baked into "
                    + pair.Value.Count + " materials because their shader or textures differ: "
                    + string.Join(", ", pair.Value) + ".");
            }

            WarnUnmatchedOverrideKeys(sourceMaterials, overrideShaderBySource, textureRows, floatRows);
        }

        // A row keyed on a name no source material carries is a typo worth
        // surfacing: the bake would otherwise complete cleanly with the
        // override silently unapplied.
        private static void WarnUnmatchedOverrideKeys(
            Material[] sourceMaterials, Dictionary<string, Shader> overrideShaderBySource,
            List<MaterialTextureOverride> textureRows, List<MaterialFloatOverride> floatRows)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (overrideShaderBySource != null)
                keys.UnionWith(overrideShaderBySource.Keys);
            if (textureRows != null)
                keys.UnionWith(textureRows
                    .Where(r => r != null && r.texture != null && !string.IsNullOrWhiteSpace(r.sourceMaterial))
                    .Select(r => r.sourceMaterial.Trim()));
            if (floatRows != null)
                keys.UnionWith(floatRows
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.sourceMaterial))
                    .Select(r => r.sourceMaterial.Trim()));

            var unmatched = keys
                .Where(k => !sourceMaterials.Any(m => MatchPrecision(k, m) > 0))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            if (unmatched.Length > 0)
                Debug.LogWarning("Jiangyu BakeHumanoid: override rows matched no source material: "
                    + string.Join(", ", unmatched) + ".");
        }

        private static Texture TextureKeyFor(Material srcMat)
        {
            var srcTexture = ExtractBaseColorTexture(srcMat);
            return srcTexture != null ? (Texture)srcTexture : (Texture)Texture2D.whiteTexture;
        }

        private static Texture2D ExtractBaseColorTexture(Material mat)
        {
            if (mat == null) return null;
            foreach (var prop in BaseColorPropertyNames)
            {
                if (mat.HasProperty(prop))
                {
                    var tex = mat.GetTexture(prop) as Texture2D;
                    if (tex != null) return tex;
                }
            }
            return mat.mainTexture as Texture2D;
        }

        private static string SanitiseAssetName(string raw)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var c in raw)
                sb.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
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
