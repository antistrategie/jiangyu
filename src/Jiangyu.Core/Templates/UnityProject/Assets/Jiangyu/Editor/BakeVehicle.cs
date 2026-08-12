using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Jiangyu.Mod
{
    /// <summary>
    /// Editor utility that bakes a vehicle prefab from an authored FBX
    /// (generic rig + meshes + animation takes).
    ///
    /// The vehicle sibling of <see cref="BakeWeapon"/> and BakeHumanoid:
    /// imports the FBX as a Generic rig with true world size baked into the
    /// bind pose (prefab root stays scale 1 like vanilla vehicles), builds an
    /// AnimatorController speaking the vanilla vehicle driver's parameter
    /// contract (dumped from aco.carrier_chassis), bakes one Menace/character
    /// material per source material slot from the authored texture sets, and
    /// writes the result as an addition prefab.
    ///
    /// Doors runtime contract: when -doorOpenClip is given, the doors layer is
    /// driven by a mod-owned Bool parameter (default name "DoorsOut"). Nothing
    /// in the game or the loader sets it (the vanilla driver's aiming
    /// parameters are write-only noise for entity-granted skills), so the mod MUST
    /// ship runtime code that sets the parameter true when the doors should
    /// open (for example on its firing skill's OnUse) and false when they
    /// should close (OnAfterUse). Doors then close on their own after
    /// -doorLingerSeconds of the parameter staying false.
    ///
    /// Batchmode (clip names are the FBX take names of the vehicle being baked):
    ///   -executeMethod Jiangyu.Mod.BakeVehicle.BakeBatch
    ///   -fbxPath Assets/Authored/&lt;vehicle&gt;/raw.fbx     (required)
    ///   -outputName &lt;vehicle&gt;/&lt;variant&gt;                (required)
    ///   -targetLength 6.0            (required. metres, longest dimension, scale is solved to hit it)
    ///   -moveClip &lt;take&gt;             (required. looping locomotion clip)
    ///   -doorOpenClip &lt;take&gt;         (optional. builds the doors layer when present)
    ///   -doorCloseClip &lt;take&gt;        (optional)
    ///   -doorsParam &lt;name&gt;           (optional. doors layer Bool parameter, default DoorsOut)
    ///   -doorLingerSeconds 2.0       (optional. doors hold open this long after the parameter drops)
    ///   -idleSpeedThreshold 0.05     (optional. driver Speed below this counts as idle)
    ///   -outputRoot Assets/Prefabs   (optional. root folder the &lt;vehicle&gt;/&lt;variant&gt; output lands under)
    ///   -dropMeshes &lt;substrings&gt;     (optional. comma-separated renderer names to delete)
    ///   -materialManifest &lt;json&gt;    (optional. {"materials":[{"name","base","normal","mask","shader","extras":[{"property","path"}],"floats":[{"property","value"}]}]} per material)
    ///   -graftNodes &lt;spec,...&gt;       (optional. &lt;importedPrefabPath&gt;@&lt;childPath&gt; sub-assemblies to copy in)
    ///   -muzzleAnchors &lt;spec,...&gt;    (optional. &lt;parentTransform&gt;:&lt;muzzleName&gt; fire-skill origin anchors)
    /// </summary>
    internal sealed class BakeVehicle : EditorWindow
    {
        // Window state. The same inputs the batch arguments carry, so neither entry
        // point is more capable than the other: a vehicle that can only be baked
        // from a command line is a vehicle nobody can iterate on.
        private DefaultAsset _fbxFolder;
        private string _fbxPath = "";
        private string _outputName = "";
        private string _outputRoot = "Assets/Prefabs";
        private float _targetLength = 6f;
        private string _moveClip = "";
        private string _doorOpenClip = "";
        private string _doorCloseClip = "";
        private string _doorsParam = "DoorsOut";
        private float _doorLingerSeconds = 2f;
        private float _idleSpeedThreshold = 0.05f;
        private string _dropMeshes = "";
        private string _graftNodes = "";
        private string _muzzleAnchors = "";
        private string _materialManifest = "";
        private Shader _overrideShader;
        private readonly List<VehicleSlot> _slotOverrides = new List<VehicleSlot>();
        private Vector2 _scroll;

        [MenuItem("Jiangyu/Bake vehicle prefab from FBX…")]
        private static void ShowWindow()
        {
            GetWindow<BakeVehicle>(true, "Bake vehicle prefab", true).minSize =
                new Vector2(560f, 640f);
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                EditorGUILayout.LabelField("Source and output", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Bakes an authored vehicle FBX into an addition prefab: a Generic rig with "
                    + "true world size in the bind pose, an AnimatorController speaking the vanilla "
                    + "vehicle driver's parameter contract, and one baked material per source "
                    + "material slot.\n\n"
                    + "The model is scaled so its longest dimension measures Target length, so that "
                    + "value is in metres and is solved for rather than guessed.",
                    MessageType.None);

                _fbxPath = EditorGUILayout.TextField(
                    new GUIContent("FBX path", "Assets-relative path to the authored FBX."),
                    _fbxPath);
                var picked = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent("… or drop the FBX", "Drag the FBX in to fill the path above."),
                    _fbxFolder, typeof(DefaultAsset), false);
                if (picked != _fbxFolder)
                {
                    _fbxFolder = picked;
                    if (picked != null) _fbxPath = AssetDatabase.GetAssetPath(picked);
                }

                _outputName = EditorGUILayout.TextField(
                    new GUIContent("Output name", "<vehicle>/<variant>, e.g. koleda_car/default."),
                    _outputName);
                _outputRoot = EditorGUILayout.TextField("Output root", _outputRoot);
                _targetLength = EditorGUILayout.FloatField(
                    new GUIContent("Target length (m)", "Longest model dimension in metres."),
                    _targetLength);

                GUILayout.Space(6);
                EditorGUILayout.LabelField("Animation takes", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Clip names are the FBX take names of this vehicle. The move clip is required "
                    + "and loops. Door clips are optional: giving an open clip is what builds the "
                    + "doors layer at all, and that layer is driven by a Bool parameter the mod's "
                    + "own runtime code has to set, because nothing in the game sets it.",
                    MessageType.None);
                _moveClip = EditorGUILayout.TextField("Move clip", _moveClip);
                _doorOpenClip = EditorGUILayout.TextField("Door open clip", _doorOpenClip);
                _doorCloseClip = EditorGUILayout.TextField("Door close clip", _doorCloseClip);
                _doorsParam = EditorGUILayout.TextField("Doors parameter", _doorsParam);
                _doorLingerSeconds = EditorGUILayout.FloatField("Doors linger (s)", _doorLingerSeconds);
                _idleSpeedThreshold = EditorGUILayout.FloatField("Idle speed threshold", _idleSpeedThreshold);

                GUILayout.Space(6);
                EditorGUILayout.LabelField("Model surgery (optional)", EditorStyles.boldLabel);
                _dropMeshes = EditorGUILayout.TextField(
                    new GUIContent("Drop meshes", "Comma-separated renderer name substrings to delete."),
                    _dropMeshes);
                _graftNodes = EditorGUILayout.TextField(
                    new GUIContent("Graft nodes", "<importedPrefabPath>@<childPath>, comma-separated."),
                    _graftNodes);
                _muzzleAnchors = EditorGUILayout.TextField(
                    new GUIContent("Muzzle anchors", "<parentTransform>:<muzzleName>, comma-separated."),
                    _muzzleAnchors);

                GUILayout.Space(6);
                EditorGUILayout.LabelField("Materials", EditorStyles.boldLabel);
                _overrideShader = (Shader)EditorGUILayout.ObjectField(
                    new GUIContent("Override shader",
                        "Blanket shader for every baked material. Leave empty for the Menace default."),
                    _overrideShader, typeof(Shader), false);
                _materialManifest = EditorGUILayout.TextField(
                    new GUIContent("Material manifest", "Optional JSON path. Window rows below win over it."),
                    _materialManifest);

                DrawSlotOverrides();

                GUILayout.Space(10);
                using (new EditorGUI.DisabledScope(
                    string.IsNullOrWhiteSpace(_fbxPath) || string.IsNullOrWhiteSpace(_outputName)
                    || string.IsNullOrWhiteSpace(_moveClip) || _targetLength <= 0f))
                {
                    if (GUILayout.Button("Bake vehicle prefab", GUILayout.Height(30)))
                        RunFromWindow();
                }
            }
        }

        private void RunFromWindow()
        {
            try
            {
                Bake(
                    fbxPath: _fbxPath.Trim(),
                    outputName: _outputName.Trim(),
                    targetLength: _targetLength,
                    moveClip: _moveClip.Trim(),
                    doorOpenClip: Blank(_doorOpenClip),
                    doorCloseClip: Blank(_doorCloseClip),
                    doorsParam: string.IsNullOrWhiteSpace(_doorsParam) ? "DoorsOut" : _doorsParam.Trim(),
                    doorLingerSeconds: _doorLingerSeconds,
                    idleSpeedThreshold: Mathf.Max(_idleSpeedThreshold, 0.001f),
                    outputRoot: string.IsNullOrWhiteSpace(_outputRoot) ? "Assets/Prefabs" : _outputRoot.Trim(),
                    dropMeshes: Split(_dropMeshes),
                    materialManifest: Blank(_materialManifest),
                    overrideShader: null,
                    graftNodes: Split(_graftNodes),
                    muzzleAnchors: Split(_muzzleAnchors),
                    slotOverrides: _slotOverrides,
                    overrideShaderAsset: _overrideShader);
                Debug.Log("Jiangyu BakeVehicle: success.");
            }
            catch (Exception ex)
            {
                Debug.LogError("Jiangyu BakeVehicle failed: " + ex);
                EditorUtility.DisplayDialog("Bake vehicle prefab",
                    "Bake failed:\n\n" + ex.Message + "\n\nThe Console carries the full trace.", "OK");
            }
        }

        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string[] Split(string s) => string.IsNullOrWhiteSpace(s)
            ? new string[0]
            : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

        // Per-material overrides authored in the window. The same ground the manifest
        // JSON covers, with the shader and textures as drag targets and the material
        // names taken from the imported FBX rather than typed from memory.
        private void DrawSlotOverrides()
        {
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Per-material overrides (optional)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Give one material slot its own shader, maps and values, keyed by the FBX material "
                + "name. Anything left empty falls back to the blanket shader above, so a row can "
                + "override the shader alone.\n\n"
                + "Values are how a material declares what its shader cannot detect: a shader "
                + "cannot read a texture's filename or tell a keyword from its absence, so a flag "
                + "such as _MaskRoughnessInverted has to arrive as a number.",
                MessageType.None);

            var removeAt = -1;
            for (int i = 0; i < _slotOverrides.Count; i++)
            {
                var row = _slotOverrides[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        row.sourceMaterial = EditorGUILayout.TextField("Slot", row.sourceMaterial);
                        if (GUILayout.Button("Remove", GUILayout.Width(70)))
                            removeAt = i;
                    }
                    row.baseMap = (Texture2D)EditorGUILayout.ObjectField(
                        "Base map", row.baseMap, typeof(Texture2D), false);
                    row.normalMap = (Texture2D)EditorGUILayout.ObjectField(
                        "Normal map", row.normalMap, typeof(Texture2D), false);
                    row.maskMap = (Texture2D)EditorGUILayout.ObjectField(
                        "Mask map", row.maskMap, typeof(Texture2D), false);
                    row.shader = (Shader)EditorGUILayout.ObjectField(
                        "Shader", row.shader, typeof(Shader), false);

                    if (row.extras == null) row.extras = new List<VehicleExtra>();
                    var dropAt = -1;
                    for (int e = 0; e < row.extras.Count; e++)
                    {
                        var extra = row.extras[e];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Extra", GUILayout.Width(40));
                            extra.propertyName = EditorGUILayout.TextField(extra.propertyName);
                            extra.texture = (Texture)EditorGUILayout.ObjectField(
                                extra.texture, typeof(Texture), false);
                            if (GUILayout.Button("x", GUILayout.Width(22)))
                                dropAt = e;
                        }
                    }
                    if (dropAt >= 0) row.extras.RemoveAt(dropAt);
                    if (GUILayout.Button("Add extra texture"))
                        row.extras.Add(new VehicleExtra());

                    if (row.floats == null) row.floats = new List<VehicleFloat>();
                    var dropFloatAt = -1;
                    for (int f = 0; f < row.floats.Count; f++)
                    {
                        var value = row.floats[f];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Value", GUILayout.Width(40));
                            value.propertyName = EditorGUILayout.TextField(value.propertyName);
                            value.value = EditorGUILayout.FloatField(value.value, GUILayout.Width(70));
                            if (GUILayout.Button("x", GUILayout.Width(22)))
                                dropFloatAt = f;
                        }
                    }
                    if (dropFloatAt >= 0) row.floats.RemoveAt(dropFloatAt);
                    if (GUILayout.Button("Add value"))
                        row.floats.Add(new VehicleFloat());
                }
            }
            if (removeAt >= 0)
                _slotOverrides.RemoveAt(removeAt);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add slot"))
                    _slotOverrides.Add(new VehicleSlot());
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_fbxPath)))
                {
                    if (GUILayout.Button("Fill from source FBX"))
                        FillSlotOverridesFromSource();
                }
            }
        }

        // Add a row per material name the FBX carries. Existing rows are kept, so
        // this is safe to press twice.
        private void FillSlotOverridesFromSource()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(_fbxPath.Trim());
            if (asset == null)
            {
                Debug.LogWarning("Jiangyu BakeVehicle: no model asset at '" + _fbxPath + "'.");
                return;
            }
            var names = asset.GetComponentsInChildren<Renderer>(includeInactive: true)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null && !string.IsNullOrEmpty(m.name))
                .Select(m => m.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            if (names.Length == 0)
            {
                Debug.LogWarning("Jiangyu BakeVehicle: '" + _fbxPath + "' has no renderer materials to list.");
                return;
            }
            var added = 0;
            foreach (var name in names)
            {
                if (_slotOverrides.Any(r => string.Equals(
                        r.sourceMaterial, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _slotOverrides.Add(new VehicleSlot { sourceMaterial = name });
                added++;
            }
            Debug.Log("Jiangyu BakeVehicle: added " + added + " slot row(s) from '" + _fbxPath + "'.");
        }

        [Serializable]
        internal class VehicleSlot
        {
            public string sourceMaterial = "";
            public Texture2D baseMap;
            public Texture2D normalMap;
            public Texture2D maskMap;
            public Shader shader;
            public List<VehicleExtra> extras = new List<VehicleExtra>();
            public List<VehicleFloat> floats = new List<VehicleFloat>();
        }

        [Serializable]
        internal class VehicleExtra
        {
            public string propertyName = "";
            public Texture texture;
        }

        [Serializable]
        internal class VehicleFloat
        {
            public string propertyName = "";
            public float value;
        }

        // Parameters the vanilla vehicle driver sets, dumped from
        // aco.carrier_chassis / aco.carrier_chassis_inside string tables.
        // A missing parameter turns the driver's Set* call into a warning, so
        // every name ships even where this controller has no state consuming it.
        private static readonly (string name, AnimatorControllerParameterType type)[] DriverParams =
        {
            ("Speed", AnimatorControllerParameterType.Float),
            ("MovementSpeed", AnimatorControllerParameterType.Float),
            ("MOVE", AnimatorControllerParameterType.Bool),
            ("Movement_Initialized", AnimatorControllerParameterType.Bool),
            ("Acceleration", AnimatorControllerParameterType.Float),
            ("Acceleration_Sign", AnimatorControllerParameterType.Float),
            ("Rotation", AnimatorControllerParameterType.Float),
            ("AngleRotation", AnimatorControllerParameterType.Float),
            ("TorqueDirection", AnimatorControllerParameterType.Float),
            ("LocomotionAngle", AnimatorControllerParameterType.Float),
            ("Leaning", AnimatorControllerParameterType.Float),
            ("SpeedLeaning", AnimatorControllerParameterType.Float),
            ("Hit", AnimatorControllerParameterType.Trigger),
            ("HitStrength", AnimatorControllerParameterType.Float),
            ("HitX", AnimatorControllerParameterType.Float),
            ("HitZ", AnimatorControllerParameterType.Float),
            ("Aiming?", AnimatorControllerParameterType.Bool),
            ("AimingWithThisSlot", AnimatorControllerParameterType.Bool),
            ("Shoot_Single", AnimatorControllerParameterType.Trigger),
            ("Shoot_Burst", AnimatorControllerParameterType.Trigger),
            ("Special_Attack_1", AnimatorControllerParameterType.Trigger),
            ("RecoilStrength", AnimatorControllerParameterType.Float),
            ("TriggerGenericWiggle", AnimatorControllerParameterType.Trigger),
            ("Synced_Cycle_Offset_InplaceTurn", AnimatorControllerParameterType.Float),
        };

        public static void BakeBatch()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name, string fallback)
            {
                for (int i = 0; i < args.Length; i++)
                    if (args[i] == name)
                    {
                        if (i == args.Length - 1)
                            throw new InvalidOperationException(name + " is missing its value.");
                        return args[i + 1];
                    }
                return fallback;
            }
            float FloatArg(string name, float fallback)
            {
                var raw = Arg(name, null);
                return raw == null ? fallback : float.Parse(raw, CultureInfo.InvariantCulture);
            }
            try
            {
                if (Arg("-targetLength", null) == null)
                    throw new InvalidOperationException("-targetLength is required (metres, longest model dimension).");
                Bake(
                    fbxPath: Arg("-fbxPath", null),
                    outputName: Arg("-outputName", null),
                    targetLength: FloatArg("-targetLength", 0f),
                    moveClip: Arg("-moveClip", null),
                    doorOpenClip: Arg("-doorOpenClip", null),
                    doorCloseClip: Arg("-doorCloseClip", null),
                    doorsParam: Arg("-doorsParam", "DoorsOut"),
                    doorLingerSeconds: FloatArg("-doorLingerSeconds", 2f),
                    // Clamped positive: at 0 the two-sided idle return
                    // (Speed < t AND Speed > -t) becomes unsatisfiable and
                    // the wheels never stop.
                    idleSpeedThreshold: Mathf.Max(FloatArg("-idleSpeedThreshold", 0.05f), 0.001f),
                    outputRoot: Arg("-outputRoot", "Assets/Prefabs"),
                    dropMeshes: Arg("-dropMeshes", "").Split(',').Where(s => s.Length > 0).ToArray(),
                    materialManifest: Arg("-materialManifest", null),
                    overrideShader: Arg("-overrideShader", null),
                    graftNodes: Arg("-graftNodes", "").Split(',').Where(s => s.Length > 0).ToArray(),
                    muzzleAnchors: Arg("-muzzleAnchors", "").Split(',').Where(s => s.Length > 0).ToArray());
                Debug.Log("Jiangyu BakeVehicle: success.");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("Jiangyu BakeVehicle failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static void Bake(
            string fbxPath, string outputName, float targetLength,
            string moveClip, string doorOpenClip, string doorCloseClip,
            string doorsParam, float doorLingerSeconds, float idleSpeedThreshold, string outputRoot,
            string[] dropMeshes, string materialManifest, string overrideShader, string[] graftNodes,
            string[] muzzleAnchors, List<VehicleSlot> slotOverrides = null,
            Shader overrideShaderAsset = null)
        {
            if (string.IsNullOrEmpty(fbxPath) || string.IsNullOrEmpty(outputName) || string.IsNullOrEmpty(moveClip))
                throw new InvalidOperationException("-fbxPath, -outputName, -targetLength, and -moveClip are required.");

            var outDir = outputRoot.TrimEnd('/') + "/" + outputName;
            Directory.CreateDirectory(outDir);

            // Pass 1: import at scale 1 and measure, so the real size can be
            // baked into the bind pose (vanilla vehicles are authored at world
            // size with a scale-1 root; the squad-bay viewer normalises root
            // scale, so anything else renders wrong there).
            var imp = (ModelImporter)AssetImporter.GetAtPath(fbxPath)
                ?? throw new InvalidOperationException("FBX not found at " + fbxPath);
            imp.animationType = ModelImporterAnimationType.Generic;
            imp.importAnimation = true;
            // Standard material import keeps the authored material NAMES on the
            // renderer slots: that is the submesh -> texture-set mapping. The
            // materials themselves get replaced by baked Menace ones below.
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.useFileScale = false;
            imp.globalScale = 1f;
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            float measured = MeasureLongestDimension(fbxPath);
            if (measured < 1e-5f)
                throw new InvalidOperationException("Measured zero-size model; cannot solve scale.");
            float scale = targetLength / measured;
            Debug.Log($"Jiangyu BakeVehicle: measured {measured:F4} at scale 1, solving globalScale={scale:F4} for target {targetLength}m.");

            imp.globalScale = scale;
            var clips = imp.defaultClipAnimations;
            foreach (var c in clips)
                if (c.name == moveClip || c.name.EndsWith("|" + moveClip, StringComparison.Ordinal)) c.loopTime = true;
            imp.clipAnimations = clips;
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            float check = MeasureLongestDimension(fbxPath);
            Debug.Log($"Jiangyu BakeVehicle: post-scale length {check:F3}m.");

            var controller = BuildController(outDir, fbxPath, moveClip, doorOpenClip, doorCloseClip,
                doorsParam, doorLingerSeconds, idleSpeedThreshold);

            // Prefab assembly.
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            try
            {
                instance.name = Path.GetFileName(outputName);

                foreach (var drop in dropMeshes)
                    foreach (var r in instance.GetComponentsInChildren<Renderer>(true)
                                 .Where(r => r != null && r.name.IndexOf(drop, StringComparison.OrdinalIgnoreCase) >= 0).ToArray())
                    {
                        if (r == null) continue;
                        Debug.Log("Jiangyu BakeVehicle: dropping mesh " + r.name);
                        UnityEngine.Object.DestroyImmediate(r.gameObject);
                    }

                // Serialise the vehicle in its closed/neutral state: the FBX's
                // node defaults capture whatever pose the exporter evaluated
                // last, so when a door-close clip exists its final frame is
                // stamped over the instance before saving.
                var closePose = FindClip(fbxPath, doorCloseClip);
                if (closePose != null)
                {
                    closePose.SampleAnimation(instance, closePose.length);
                    Debug.Log("Jiangyu BakeVehicle: stamped rest pose from '" + closePose.name + "' end frame.");
                }

                ValidateOrientation(instance);

                // Graft functional sub-assemblies from imported vanilla prefabs
                // (spec: <importedPrefabPath>@<child/transform/path>). The copied
                // subtree keeps its __jiangyu_scripts sentinel, so the loader
                // restores the game's components on it at runtime. Placed at the
                // node's source-prefab-root-relative pose, which lines up when
                // both vehicles are grounded and centred.
                var graftRoots = new List<Transform>();
                foreach (var spec in graftNodes)
                {
                    var parts = spec.Split('@');
                    if (parts.Length < 2)
                        throw new InvalidOperationException("-graftNodes entries must be <prefabPath>@<childPath>[@x;y;z]: " + spec);
                    var assetPath = parts[0];
                    var childPath = parts[1];
                    var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath)
                        ?? throw new InvalidOperationException("graft source prefab not found: " + assetPath);
                    var node = srcPrefab.transform.Find(childPath)
                        ?? throw new InvalidOperationException("graft node '" + childPath + "' not found in " + assetPath);
                    var copy = UnityEngine.Object.Instantiate(node.gameObject, instance.transform);
                    copy.name = node.name;
                    var rel = srcPrefab.transform.worldToLocalMatrix * node.localToWorldMatrix;
                    copy.transform.localPosition = rel.GetColumn(3);
                    copy.transform.localRotation = rel.rotation;
                    copy.transform.localScale = rel.lossyScale;
                    if (parts.Length >= 3)
                    {
                        var xyz = parts[2].Split(';').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        copy.transform.localPosition = new Vector3(xyz[0], xyz[1], xyz[2]);
                    }
                    graftRoots.Add(copy.transform);
                    Debug.Log("Jiangyu BakeVehicle: grafted '" + node.name + "' from " + Path.GetFileName(assetPath)
                        + " at local " + copy.transform.localPosition);
                }

                BakeMaterials(instance, materialManifest, outDir, graftRoots, overrideShader, slotOverrides, overrideShaderAsset);

                foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    // Ripped skinned meshes carry bad localBounds and get
                    // frustum-culled; recompute each frame like the mech bake.
                    smr.updateWhenOffscreen = true;
                }

                // Match native units' rendering layer mask (1). Model imports can
                // leave bit 8 set (an HDRP decal layer), which projects road and
                // other ground decals onto the vehicle. Grafted vanilla subtrees
                // keep whatever mask they shipped with.
                foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
                    if (!IsUnderAny(r.transform, graftRoots))
                        r.renderingLayerMask = 1;

                // Named projectile-origin anchors that fire skills resolve via
                // the MuzzleType enum (Muzzle -> "muzzle", Muzzle2 -> "muzzle2",
                // ...). Spec: <parentTransform>:<muzzleName>[,...], a zero-offset
                // child is parented onto each named node so it inherits the
                // node's animated pose.
                var allTransforms = instance.GetComponentsInChildren<Transform>(true);
                foreach (var spec in muzzleAnchors)
                {
                    var parts = spec.Split(':');
                    if (parts.Length != 2)
                        throw new InvalidOperationException("-muzzleAnchors entries must be <parentTransform>:<muzzleName>: " + spec);
                    var parent = allTransforms.FirstOrDefault(t => t != null && t.name == parts[0]);
                    if (parent == null)
                    {
                        Debug.LogWarning("Jiangyu BakeVehicle: muzzle anchor parent '" + parts[0] + "' not found.");
                        continue;
                    }
                    if (parent.Find(parts[1]) != null) continue;
                    var anchor = new GameObject(parts[1]);
                    anchor.transform.SetParent(parent, worldPositionStays: false);
                    Debug.Log("Jiangyu BakeVehicle: muzzle anchor '" + parts[1] + "' on '" + parts[0] + "'.");
                }

                // clock target for the doors linger state (see BuildController)
                if (instance.transform.Find("__jiangyu_timer") == null)
                    new GameObject("__jiangyu_timer").transform.SetParent(instance.transform, false);

                var animator = instance.GetComponentInChildren<Animator>();
                if (animator == null) animator = instance.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                var avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
                if (avatar != null) animator.avatar = avatar;

                var prefabPath = outDir + "/main.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("Jiangyu BakeVehicle: wrote " + prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static bool IsUnderAny(Transform t, List<Transform> roots)
            => roots.Any(root => root != null && t.IsChildOf(root));

        // MENACE vehicle convention (read off el.carrier_open_transport: rear
        // door at local -Z, wheels at min Y): forward = +Z, up = +Y, grounded
        // at y=0. Orientation is the authored FBX's job. These are heuristic
        // warnings only; a deliberately tall or short vehicle can ignore them.
        private static void ValidateOrientation(GameObject instance)
        {
            var bounds = GeometryBounds(instance);
            var s = bounds.size;
            if (s.z < s.x)
                Debug.LogWarning($"Jiangyu BakeVehicle: model is wider (X) than long (Z) (size {s}). MENACE vehicles face +Z. If the vehicle should be longer than wide, check the authored FBX's orientation. Ignore for intentionally wide vehicles.");
            if (Mathf.Abs(bounds.min.y) > 0.25f)
                Debug.LogWarning($"Jiangyu BakeVehicle: model is not grounded (bounds min.y = {bounds.min.y:F3}); wheels should sit at y=0 in the authored FBX.");
        }

        private static AnimationClip FindClip(string fbxPath, string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return null;
            return AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview", StringComparison.Ordinal))
                .FirstOrDefault(c => c.name == clipName || c.name.EndsWith("|" + clipName, StringComparison.Ordinal));
        }

        private static float MeasureLongestDimension(string fbxPath)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            try
            {
                var s = GeometryBounds(inst).size;
                return Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            }
            finally { UnityEngine.Object.DestroyImmediate(inst); }
        }

        // World-space bounds from actual mesh geometry. Renderer.bounds is not
        // trustworthy here: ripped skinned meshes routinely carry garbage
        // serialised localBounds, and a scale solve computed from them would be
        // wrong while the post-scale sanity log (reading the same bounds)
        // self-confirms the wrong answer.
        private static Bounds GeometryBounds(GameObject instance)
        {
            var bounds = new Bounds();
            bool first = true;
            void Add(Vector3 p)
            {
                if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                else bounds.Encapsulate(p);
            }
            foreach (var mf in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) Add(m.MultiplyPoint3x4(v));
            }
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                try
                {
                    // BakeMesh with useScale outputs posed vertices in the
                    // renderer's space including its scale, so only rotation and
                    // position remain to bring them to world space.
                    smr.BakeMesh(baked, true);
                    var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                    foreach (var v in baked.vertices) Add(m.MultiplyPoint3x4(v));
                }
                finally { UnityEngine.Object.DestroyImmediate(baked); }
            }
            return bounds;
        }

        private static AnimatorController BuildController(
            string outDir, string fbxPath, string moveClip, string doorOpenClip, string doorCloseClip,
            string doorsParam, float doorLingerSeconds, float idleSpeedThreshold)
        {
            var all = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview", StringComparison.Ordinal)).ToList();
            AnimationClip Find(string n) => all.FirstOrDefault(c => c.name == n || c.name.EndsWith("|" + n, StringComparison.Ordinal));
            var move = Find(moveClip);
            var doorOpen = Find(doorOpenClip);
            var doorClose = Find(doorCloseClip);
            Debug.Log($"Jiangyu BakeVehicle: clips move={move?.name} doorOpen={doorOpen?.name} doorClose={doorClose?.name} (of {all.Count})");
            if (move == null)
                throw new InvalidOperationException("Move clip '" + moveClip + "' not found in FBX takes: " + string.Join(", ", all.Select(c => c.name)));

            // The controller lives beside the variant's prefab: a shared
            // per-vehicle location would leave every other variant's prefab
            // dangling on a destroyed GUID after each rebake.
            var path = outDir + "/vehicle.controller";
            AssetDatabase.DeleteAsset(path);
            var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
            foreach (var (name, type) in DriverParams)
                ac.AddParameter(name, type);

            // Base layer: static idle <-> rolling wheels on the driver's Speed.
            // writeDefaultValues stays OFF everywhere: a state that animates
            // nothing must leave the prefab's serialised rest pose alone
            // instead of stomping every bone any other clip touches.
            var sm = ac.layers[0].stateMachine;
            var idle = sm.AddState("Idle");
            idle.writeDefaultValues = false;
            // The jy_empty_ prefix marks an authored-empty clip: the compile's
            // clip restoration leaves it alone silently instead of warning
            // that no game clip matches (playing empty is the intent here,
            // the idle state must not disturb the rest pose).
            idle.motion = new AnimationClip { name = "jy_empty_vehicle_idle" };
            AssetDatabase.AddObjectToAsset(idle.motion, ac);
            var moveState = sm.AddState("Move");
            moveState.writeDefaultValues = false;
            moveState.motion = move;
            sm.defaultState = idle;
            // Move on |Speed| beyond the threshold: the driver feeds a signed
            // Speed and a reversing vehicle must roll its wheels too. Unity ORs
            // across transitions and ANDs within one, so forward and reverse get
            // a transition each and the idle return needs both bounds at once.
            var toMove = idle.AddTransition(moveState);
            toMove.hasExitTime = false; toMove.duration = 0.05f;
            toMove.AddCondition(AnimatorConditionMode.Greater, idleSpeedThreshold, "Speed");
            var toMoveReverse = idle.AddTransition(moveState);
            toMoveReverse.hasExitTime = false; toMoveReverse.duration = 0.05f;
            toMoveReverse.AddCondition(AnimatorConditionMode.Less, -idleSpeedThreshold, "Speed");
            var toIdle = moveState.AddTransition(idle);
            toIdle.hasExitTime = false; toIdle.duration = 0.1f;
            toIdle.AddCondition(AnimatorConditionMode.Less, idleSpeedThreshold, "Speed");
            toIdle.AddCondition(AnimatorConditionMode.Greater, -idleSpeedThreshold, "Speed");

            // Doors layer: closed at rest, driven by the mod-owned doors
            // parameter (see the doors runtime contract in the class header).
            if (doorOpen != null)
            {
                var doorsSm = new AnimatorStateMachine { name = "Doors", hideFlags = HideFlags.HideInHierarchy };
                AssetDatabase.AddObjectToAsset(doorsSm, ac);
                ac.AddLayer(new AnimatorControllerLayer
                {
                    name = "Doors",
                    defaultWeight = 1f,
                    stateMachine = doorsSm,
                });
                if (!ac.parameters.Any(x => x.name == doorsParam))
                    ac.AddParameter(doorsParam, AnimatorControllerParameterType.Bool);

                var empty = doorsSm.AddState("Empty");
                empty.writeDefaultValues = false;
                doorsSm.defaultState = empty;
                var open = doorsSm.AddState("DoorsOpen");
                open.writeDefaultValues = false;
                open.motion = doorOpen;
                var toOpen = empty.AddTransition(open);
                toOpen.hasExitTime = false; toOpen.duration = 0.05f;
                toOpen.AddCondition(AnimatorConditionMode.If, 0, doorsParam);
                if (doorClose != null)
                {
                    // Hysteresis: the doors signal blips during and after a
                    // firing sequence, and following it literally makes the
                    // doors flap. After the open swing the doors hold in a
                    // Linger state whose motion is a LOOPING constant curve on
                    // a dedicated dummy child (so no real bone is disturbed),
                    // sized to the linger duration. The exit transition sits
                    // below normalised time 1 on a looping clip, so Mecanim
                    // re-evaluates it every loop, so the doors close on the
                    // first cycle where the parameter reads false, however
                    // long the signal stayed up.
                    var lingerClip = new AnimationClip { name = "doors_linger_hold" };
                    var flat = AnimationCurve.Constant(0f, Mathf.Max(doorLingerSeconds, 0.1f), 0f);
                    lingerClip.SetCurve("__jiangyu_timer", typeof(Transform), "localPosition.x", flat);
                    var settings = AnimationUtility.GetAnimationClipSettings(lingerClip);
                    settings.loopTime = true;
                    AnimationUtility.SetAnimationClipSettings(lingerClip, settings);
                    AssetDatabase.AddObjectToAsset(lingerClip, ac);
                    var linger = doorsSm.AddState("DoorsLinger");
                    linger.writeDefaultValues = false;
                    linger.motion = lingerClip;

                    var toLinger = open.AddTransition(linger);
                    toLinger.hasExitTime = true; toLinger.exitTime = 0.95f; toLinger.duration = 0.05f;

                    var close = doorsSm.AddState("DoorsClose");
                    close.writeDefaultValues = false;
                    close.motion = doorClose;
                    var toClose = linger.AddTransition(close);
                    toClose.hasExitTime = true; toClose.exitTime = 0.95f; toClose.duration = 0.1f;
                    toClose.AddCondition(AnimatorConditionMode.IfNot, 0, doorsParam);

                    var closeOut = close.AddTransition(empty);
                    closeOut.hasExitTime = true; closeOut.exitTime = 0.98f; closeOut.duration = 0.02f;
                    // doors requested again mid-close re-open
                    var reOpen = close.AddTransition(open);
                    reOpen.hasExitTime = false; reOpen.duration = 0.05f;
                    reOpen.AddCondition(AnimatorConditionMode.If, 0, doorsParam);
                }
                else
                {
                    var closeOut = open.AddTransition(empty);
                    closeOut.hasExitTime = false; closeOut.duration = 0.2f;
                    closeOut.AddCondition(AnimatorConditionMode.IfNot, 0, doorsParam);
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Jiangyu BakeVehicle: wrote " + path);
            return ac;
        }

        // One Menace/character material per source material slot. The diffuse is
        // read from the FBX itself (the authored source materials carry their
        // texture bindings through the import). Normals and masks come from the
        // -materialManifest JSON: {"materials": [{"name": "<materialName>",
        // "base": "path", "normal": "path", "mask": "path"}, ...]}: the texture
        // fields are optional, "base" overrides the FBX-bound diffuse. Path
        // conventions of whatever asset source the mod rips from are the mod
        // build script's business, not this tool's. A missing mask falls back to
        // a neutral 1x1 (the Menace shader's default mask reads Metallic=1 and
        // renders chrome). Grafted vanilla subtrees are left untouched so the
        // loader's name-matched material rebind still recognises their slots.
        [Serializable] private class ManifestExtra { public string property; public string path; }
        [Serializable] private class ManifestFloat { public string property; public float value; }
        [Serializable] private class ManifestEntry { public string name; public string @base; public string normal; public string mask; public string shader; public List<ManifestExtra> extras; public List<ManifestFloat> floats; }
        [Serializable] private class ManifestFile { public List<ManifestEntry> materials; }

        // Resolve a shader from a manifest entry or a batch argument. Accepts
        // an asset path (Assets/Shaders/DollToon.shader) or a shader name
        // (Womenace/DollToon). An absent value returns null so the caller falls
        // through to its next choice. A value that resolves to nothing throws
        // rather than falling back, so a typo cannot quietly ship a vehicle on
        // the vanilla shader.
        private static Shader ResolveOverrideShader(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var byPath = AssetDatabase.LoadAssetAtPath<Shader>(value);
            if (byPath != null) return byPath;
            var byName = Shader.Find(value);
            if (byName != null) return byName;
            throw new InvalidOperationException(
                "Jiangyu BakeVehicle: shader not found: '" + value
                + "'. Give a shader asset path or a shader name.");
        }

        private static void BakeMaterials(GameObject instance, string materialManifest, string outDir,
            List<Transform> graftRoots, string overrideShader, List<VehicleSlot> slotOverrides = null,
            Shader overrideShaderAsset = null)
        {
            // Purge baked_*.mat left by earlier runs before generating: a
            // renamed material or a debug bake otherwise leaves an orphan
            // beside the live set, and committed orphan .mat files are the
            // exact surface where machine-local stub-shader GUIDs go dangling
            // (the magenta-model failure).
            foreach (var stale in Directory.GetFiles(outDir, "baked_*.mat"))
                AssetDatabase.DeleteAsset(stale.Replace('\\', '/'));

            var manifest = LoadManifest(materialManifest);

            // Window rows over the manifest: an in-window edit wins over a file the
            // modder may not have open. A row naming a slot but overriding nothing is
            // inert, which is what makes "Fill from source FBX" safe to press.
            // Case-insensitive like the humanoid bake's row matching, so a
            // hand-typed slot name cannot miss by capitalisation alone.
            var rows = new Dictionary<string, VehicleSlot>(StringComparer.OrdinalIgnoreCase);
            if (slotOverrides != null)
            {
                foreach (var row in slotOverrides)
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.sourceMaterial)) continue;
                    rows[row.sourceMaterial.Trim()] = row;
                }
            }
            var usedManifestKeys = new HashSet<string>();
            var usedRowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A material's own manifest "shader" wins, then the blanket
            // -overrideShader, then MENACE's vanilla character shader. The
            // window hands its blanket shader over as the object itself, so a
            // shader with no loadable asset path still resolves.
            var defaultShader = overrideShaderAsset ?? ResolveOverrideShader(overrideShader)
                ?? Shader.Find("Menace/character") ?? Shader.Find("Standard");
            var maskDefault = EnsureDefaultTexture(
                "Assets/Materials/Jiangyu/_jiangyu_neutral_mask.png",
                new Color32(0, 255, 0, 128), isNormalMap: false, linear: true);
            // Keyed by source material IDENTITY, not name: ripped FBXes carry
            // duplicate and punctuation-variant material names, and a name key
            // would collapse or overwrite them. Asset filenames are deduped for
            // the same reason.
            var cache = new Dictionary<Material, Material>();
            var usedFileNames = new HashSet<string>();
            int slotFallbacks = 0;
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (IsUnderAny(r.transform, graftRoots)) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src != null && cache.TryGetValue(src, out var cached))
                    {
                        mats[i] = cached;
                        continue;
                    }
                    var srcName = src != null ? src.name : r.name + "_" + i + "_slot" + (++slotFallbacks);
                    manifest.TryGetValue(srcName, out var entry);
                    if (entry != null) usedManifestKeys.Add(srcName);
                    rows.TryGetValue(srcName, out var row);
                    if (row != null) usedRowKeys.Add(srcName);
                    var shaderForEntry = row?.shader ?? ResolveOverrideShader(entry?.shader) ?? defaultShader;
                    var baked = new Material(shaderForEntry) { name = "baked_" + srcName };

                    var diffuse = row?.baseMap
                        ?? (entry?.@base != null ? LoadManifestTexture(entry.@base, srcName, "base", EnsureSrgb) : ResolveDiffuse(src));
                    if (diffuse != null)
                    {
                        baked.SetTexture("_BaseColorMap", diffuse);
                        baked.SetTexture("_BaseMap", diffuse);
                        baked.SetTexture("_MainTex", diffuse);
                    }
                    else
                    {
                        Debug.LogWarning("Jiangyu BakeVehicle: no diffuse texture resolved for material '" + srcName + "'.");
                    }

                    var normal = row?.normalMap
                        ?? (entry?.normal != null ? LoadManifestTexture(entry.normal, srcName, "normal", EnsureNormal) : null);
                    if (normal != null && baked.HasProperty("_NormalMap")) baked.SetTexture("_NormalMap", normal);

                    var mask = row?.maskMap
                        ?? (entry?.mask != null ? LoadManifestTexture(entry.mask, srcName, "mask", EnsureLinear) : null);
                    AssignFirst(baked, mask ?? maskDefault, "_MaskMap", "_Mask", "_MetallicGlossMap");

                    // Maps a custom shader declares beyond base, normal and
                    // mask. A property the shader does not declare is a typo
                    // worth surfacing: SetTexture accepts it silently otherwise.
                    if (entry?.extras != null)
                    {
                        foreach (var extra in entry.extras)
                        {
                            if (extra == null) continue;
                            if (string.IsNullOrWhiteSpace(extra.property) || string.IsNullOrWhiteSpace(extra.path))
                                continue;
                            var extraTex = AssetDatabase.LoadAssetAtPath<Texture>(extra.path);
                            if (extraTex == null)
                                throw new InvalidOperationException(
                                    "Jiangyu BakeVehicle: manifest extra texture not found: '" + extra.path + "'.");
                            if (!baked.HasProperty(extra.property))
                            {
                                Debug.LogWarning(
                                    "Jiangyu BakeVehicle: shader '" + baked.shader.name
                                    + "' has no texture property '" + extra.property
                                    + "', so the assignment for material '" + srcName + "' is skipped.");
                                continue;
                            }
                            baked.SetTexture(extra.property, extraTex);
                        }
                    }

                    // Flags and values a custom shader declares. A shader cannot
                    // read a texture's filename or tell a keyword from its absence,
                    // so whether a mask holds roughness or smoothness, or whether a
                    // material takes a particular shading path, arrives as a number.
                    if (entry?.floats != null)
                    {
                        foreach (var value in entry.floats)
                        {
                            if (value == null || string.IsNullOrWhiteSpace(value.property)) continue;
                            if (!baked.HasProperty(value.property))
                            {
                                Debug.LogWarning(
                                    "Jiangyu BakeVehicle: shader '" + baked.shader.name
                                    + "' has no property '" + value.property
                                    + "', so the value for material '" + srcName + "' is skipped.");
                                continue;
                            }
                            baked.SetFloat(value.property, value.value);
                        }
                    }

                    // The window's own rows last, so a row and a manifest entry naming
                    // the same property leave the row's value in place.
                    if (row != null)
                    {
                        foreach (var extra in row.extras ?? new List<VehicleExtra>())
                        {
                            if (extra == null || extra.texture == null) continue;
                            if (string.IsNullOrWhiteSpace(extra.propertyName)) continue;
                            if (!baked.HasProperty(extra.propertyName))
                            {
                                Debug.LogWarning(
                                    "Jiangyu BakeVehicle: shader '" + baked.shader.name
                                    + "' has no texture property '" + extra.propertyName
                                    + "', so the assignment for material '" + srcName + "' is skipped.");
                                continue;
                            }
                            baked.SetTexture(extra.propertyName, extra.texture);
                        }
                        foreach (var value in row.floats ?? new List<VehicleFloat>())
                        {
                            if (value == null || string.IsNullOrWhiteSpace(value.propertyName)) continue;
                            if (!baked.HasProperty(value.propertyName))
                            {
                                Debug.LogWarning(
                                    "Jiangyu BakeVehicle: shader '" + baked.shader.name
                                    + "' has no property '" + value.propertyName
                                    + "', so the value for material '" + srcName + "' is skipped.");
                                continue;
                            }
                            baked.SetFloat(value.propertyName, value.value);
                        }
                    }

                    // Native unit materials opt out of receiving decals so
                    // road and other ground decals do not project onto them.
                    if (baked.HasProperty("_SupportDecals")) baked.SetFloat("_SupportDecals", 0f);
                    baked.EnableKeyword("_DISABLE_DECALS");

                    var fileName = Sanitise(srcName);
                    for (int n = 2; !usedFileNames.Add(fileName); n++)
                        fileName = Sanitise(srcName) + "_" + n;
                    AssetDatabase.CreateAsset(baked, outDir + "/baked_" + fileName + ".mat");
                    if (src != null) cache[src] = baked;
                    mats[i] = baked;
                }
                r.sharedMaterials = mats;
            }
            foreach (var key in manifest.Keys.Where(k => !usedManifestKeys.Contains(k)))
                Debug.LogWarning("Jiangyu BakeVehicle: manifest entry '" + key + "' matched no material slot on the model (typo?).");
            foreach (var key in rows.Keys.Where(k => !usedRowKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                Debug.LogWarning("Jiangyu BakeVehicle: per-material override row '" + key + "' matched no material slot on the model (typo?).");
            Debug.Log("Jiangyu BakeVehicle: baked " + usedFileNames.Count + " material(s) (" + manifest.Count + " manifest entr(ies)).");
        }

        private static Texture2D LoadManifestTexture(string path, string material, string kind, Func<string, Texture2D> load)
        {
            var tex = load(path);
            if (tex == null)
                Debug.LogWarning("Jiangyu BakeVehicle: manifest " + kind + " texture for '" + material + "' failed to load: " + path);
            return tex;
        }

        private static Dictionary<string, ManifestEntry> LoadManifest(string path)
        {
            var result = new Dictionary<string, ManifestEntry>();
            if (string.IsNullOrEmpty(path)) return result;
            if (!File.Exists(path))
                throw new InvalidOperationException("-materialManifest not found: " + path);
            var parsed = JsonUtility.FromJson<ManifestFile>(File.ReadAllText(path));
            if (parsed?.materials == null)
                throw new InvalidOperationException("-materialManifest has no \"materials\" list (expected {\"materials\":[{\"name\",...}]}): " + path);
            foreach (var entry in parsed.materials)
            {
                if (string.IsNullOrEmpty(entry.name))
                {
                    Debug.LogWarning("Jiangyu BakeVehicle: manifest entry without a \"name\" ignored.");
                    continue;
                }
                result[entry.name] = entry;
            }
            return result;
        }

        private static Texture2D ResolveDiffuse(Material src)
        {
            var bound = src != null ? src.mainTexture as Texture2D : null;
            if (bound == null && src != null)
                foreach (var prop in new[] { "_BaseColorMap", "_BaseMap", "_MainTex" })
                    if (src.HasProperty(prop) && src.GetTexture(prop) is Texture2D t) { bound = t; break; }
            return bound == null ? null : EnsureSrgb(AssetDatabase.GetAssetPath(bound));
        }

        private static void AssignFirst(Material mat, Texture2D tex, params string[] props)
        {
            if (tex == null) return;
            foreach (var p in props)
                if (mat.HasProperty(p)) { mat.SetTexture(p, tex); return; }
        }

        private static Texture2D EnsureSrgb(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && (!importer.sRGBTexture || importer.textureType != TextureImporterType.Default))
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D EnsureNormal(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && importer.textureType != TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Mask maps are data, not colour: force linear sampling.
        private static Texture2D EnsureLinear(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && (importer.sRGBTexture || importer.textureType != TextureImporterType.Default))
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
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
                tex.SetPixels32(new[] { colour });
                tex.Apply();
                File.WriteAllBytes(assetPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
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
                    importer.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private static string Sanitise(string s) =>
            new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }
}
