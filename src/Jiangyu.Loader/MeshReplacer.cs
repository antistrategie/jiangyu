using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Jiangyu.Loader;

public class MeshReplacer
{
    // game mesh name → (replacement mesh, bone names in order)
    private readonly Dictionary<string, ReplacementMesh> _replacements = new();
    private readonly Dictionary<string, ReplacementPrefab> _prefabReplacements = new();
    private readonly Dictionary<int, DrivenReplacement> _drivenReplacements = new();
    private readonly Dictionary<string, Texture2D> _replacementTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Material[]> _materialCache = new(StringComparer.Ordinal);

    // Keep strong references to prevent GC
    private readonly List<UnityEngine.Object> _pinned = new();

    private class ReplacementMesh
    {
        public Mesh Mesh;
        public string[] BoneNames;
        public string TexturePrefix;

        public ReplacementMesh(Mesh mesh, string[] boneNames, string texturePrefix)
        {
            Mesh = mesh;
            BoneNames = boneNames;
            TexturePrefix = texturePrefix;
        }
    }

    private class PreparedMeshAssignment
    {
        public Mesh Mesh;
        public Bounds Bounds;
        public bool UpdateWhenOffscreen;
    }

    private class ReplacementPrefab
    {
        public GameObject Template;
        public string PrefabName;
        public string[] BoneNames;

        public ReplacementPrefab(GameObject template, string prefabName, string[] boneNames)
        {
            Template = template;
            PrefabName = prefabName;
            BoneNames = boneNames;
        }
    }

    private class DrivenReplacement
    {
        public GameObject EntityRoot;
        public GameObject ReplacementRoot;
        public Transform SourceSkeletonRoot;
        public Transform ReplacementSkeletonRoot;
        public TransformPair[] BonePairs;
        public Renderer[] HiddenRenderers;
        public Renderer[] ReplacementRenderers;
        public SkinnedMeshRenderer[] ReplacementSkinnedRenderers;
        public string PrefabName;
        public bool LoggedInitialSync;
    }

    private class TransformPair
    {
        public string Name;
        public Transform Source;
        public Transform Target;
    }

    private class CompiledMeshMetadataRecord
    {
        public string[] BoneNames;
        public string TexturePrefix;
    }

    private static string FormatVector3(Vector3 v)
        => $"({v.x:F4}, {v.y:F4}, {v.z:F4})";

    private static string[] GetBoneNames(Transform[] bones)
    {
        var names = new string[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            names[i] = bones[i]?.name ?? "";
        return names;
    }

    private static bool IsLiveSceneRenderer(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.sharedMesh == null)
            return false;

        if (smr.hideFlags != HideFlags.None)
            return false;
        if (smr.gameObject.hideFlags != HideFlags.None)
            return false;

        var scene = smr.gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private static bool IsAlreadyProcessedMesh(Mesh mesh)
        => mesh != null && mesh.name.EndsWith(" [jiangyu]", StringComparison.Ordinal);

    private static GameObject FindEntityRoot(SkinnedMeshRenderer smr)
    {
        if (smr == null)
            return null;

        Transform bestNamedCandidate = null;
        var current = smr.transform;
        while (current != null)
        {
            if (current.GetComponent<Animator>() != null)
                return current.gameObject;

            var name = current.name;
            if (!string.IsNullOrEmpty(name) &&
                !name.Contains("_LOD", StringComparison.OrdinalIgnoreCase) &&
                (name.StartsWith("rmc_", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("soldier", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("enemy", StringComparison.OrdinalIgnoreCase)))
            {
                bestNamedCandidate = current;
            }

            current = current.parent;
        }

        if (bestNamedCandidate != null)
            return bestNamedCandidate.gameObject;

        return smr.rootBone?.root?.gameObject ?? smr.transform.root.gameObject;
    }


    private static bool BindPosesMatch(Matrix4x4[] a, Matrix4x4[] b, float epsilon = 1e-4f)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            var ma = a[i];
            var mb = b[i];
            if (Math.Abs(ma.m00 - mb.m00) > epsilon || Math.Abs(ma.m01 - mb.m01) > epsilon || Math.Abs(ma.m02 - mb.m02) > epsilon || Math.Abs(ma.m03 - mb.m03) > epsilon ||
                Math.Abs(ma.m10 - mb.m10) > epsilon || Math.Abs(ma.m11 - mb.m11) > epsilon || Math.Abs(ma.m12 - mb.m12) > epsilon || Math.Abs(ma.m13 - mb.m13) > epsilon ||
                Math.Abs(ma.m20 - mb.m20) > epsilon || Math.Abs(ma.m21 - mb.m21) > epsilon || Math.Abs(ma.m22 - mb.m22) > epsilon || Math.Abs(ma.m23 - mb.m23) > epsilon ||
                Math.Abs(ma.m30 - mb.m30) > epsilon || Math.Abs(ma.m31 - mb.m31) > epsilon || Math.Abs(ma.m32 - mb.m32) > epsilon || Math.Abs(ma.m33 - mb.m33) > epsilon)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldStripLeadingRoot(string[] gameBoneNames, string[] replacementBoneNames)
    {
        if (replacementBoneNames.Length != gameBoneNames.Length + 1)
            return false;
        if (!string.Equals(replacementBoneNames[0], "Root", StringComparison.Ordinal))
            return false;

        for (int i = 0; i < gameBoneNames.Length; i++)
        {
            if (!string.Equals(gameBoneNames[i], replacementBoneNames[i + 1], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool TryCopyGeometryOntoExistingMesh(MelonLogger.Instance log, Mesh targetMesh, Mesh replacementMesh)
    {
        if (targetMesh == null || replacementMesh == null)
            return false;
        if (!replacementMesh.isReadable)
        {
            log.Warning("  [DIAG] replacement mesh is not readable; cannot copy geometry");
            return false;
        }

        try
        {
            var vertices = replacementMesh.vertices;
            var normals = replacementMesh.normals;
            var tangents = replacementMesh.tangents;
            var uv = replacementMesh.uv;
            var uv2 = replacementMesh.uv2;
            var colors32 = replacementMesh.colors32;
            var triangles = replacementMesh.triangles;

            targetMesh.Clear();
            targetMesh.indexFormat = replacementMesh.indexFormat;
            targetMesh.vertices = vertices;
            if (normals != null && normals.Length == vertices.Length)
                targetMesh.normals = normals;
            if (tangents != null && tangents.Length == vertices.Length)
                targetMesh.tangents = tangents;
            if (uv != null && uv.Length == vertices.Length)
                targetMesh.uv = uv;
            if (uv2 != null && uv2.Length == vertices.Length)
                targetMesh.uv2 = uv2;
            if (colors32 != null && colors32.Length == vertices.Length)
                targetMesh.colors32 = colors32;
            targetMesh.triangles = triangles;
            targetMesh.RecalculateBounds();

            log.Msg($"  [DIAG] in-place geometry copy ok verts={vertices.Length} tris={triangles.Length}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"  [DIAG] in-place geometry copy failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static Mesh InstantiateMeshClone(Mesh sourceMesh)
    {
        var clone = UnityEngine.Object.Instantiate(sourceMesh);
        clone.name = $"{sourceMesh.name} [jiangyu]";
        return clone;
    }

    private static Matrix4x4[] BuildBindPosesFromLiveBones(Transform rendererTransform, Transform[] bones)
    {
        var bindPoses = new Matrix4x4[bones.Length];
        var rendererLocalToWorld = rendererTransform.localToWorldMatrix;
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            bindPoses[i] = bone != null
                ? bone.worldToLocalMatrix * rendererLocalToWorld
                : Matrix4x4.identity;
        }

        return bindPoses;
    }

    private static bool TryReadNativeBindPoses(MelonLogger.Instance log, Mesh sourceMesh, out Matrix4x4[] bindPoses)
    {
        bindPoses = null;
        if (sourceMesh == null)
            return false;

        try
        {
            var count = sourceMesh.bindposeCount;
            if (count <= 0)
                return false;

            var ptr = sourceMesh.GetBindposesArray();
            if (ptr == IntPtr.Zero)
                return false;

            bindPoses = new Matrix4x4[count];
            var stride = Marshal.SizeOf<Matrix4x4>();
            for (int i = 0; i < count; i++)
                bindPoses[i] = Marshal.PtrToStructure<Matrix4x4>(IntPtr.Add(ptr, i * stride));
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to read native bindposes from '{sourceMesh.name}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryPrepareReplacementMeshForLiveBones(MelonLogger.Instance log, SkinnedMeshRenderer smr, Mesh targetMesh, Mesh replacementMesh, Transform[] newBones, out Mesh preparedMesh)
    {
        preparedMesh = null;
        if (replacementMesh == null)
            return false;

        try
        {
            var runtimeMesh = InstantiateMeshClone(replacementMesh);
            Matrix4x4[] bindPosesToUse = null;
            if (!TryReadNativeBindPoses(log, targetMesh, out bindPosesToUse) || bindPosesToUse == null || bindPosesToUse.Length != newBones.Length)
            {
                Matrix4x4[] replacementBindPoses = null;
                if (TryReadNativeBindPoses(log, replacementMesh, out replacementBindPoses) &&
                    replacementBindPoses != null &&
                    replacementBindPoses.Length == newBones.Length)
                {
                    bindPosesToUse = replacementBindPoses;
                }
            }

            if (bindPosesToUse == null || bindPosesToUse.Length != newBones.Length)
            {
                if (bindPosesToUse == null || bindPosesToUse.Length != newBones.Length)
                    bindPosesToUse = BuildBindPosesFromLiveBones(smr.transform, newBones);
            }

            runtimeMesh.bindposes = bindPosesToUse;
            preparedMesh = runtimeMesh;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to prepare replacement bindposes for '{replacementMesh.name}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryUseReplacementMeshDirectly(MelonLogger.Instance log, Mesh targetMesh, Mesh replacementMesh, string[] targetBoneNames, string[] replacementBoneNames)
    {
        if (targetMesh == null || replacementMesh == null)
            return false;
        if (targetBoneNames == null || replacementBoneNames == null)
            return false;
        if (targetBoneNames.Length != replacementBoneNames.Length)
            return false;

        for (int i = 0; i < targetBoneNames.Length; i++)
        {
            if (!string.Equals(targetBoneNames[i], replacementBoneNames[i], StringComparison.Ordinal))
                return false;
        }

        if (!TryReadNativeBindPoses(log, targetMesh, out var targetBindPoses) || targetBindPoses == null)
            return false;
        if (!TryReadNativeBindPoses(log, replacementMesh, out var replacementBindPoses) || replacementBindPoses == null)
            return false;

        return BindPosesMatch(targetBindPoses, replacementBindPoses);
    }

    public int LoadBundles(string modsDir, MelonLogger.Instance log)
    {
        var bundleCount = 0;

        if (!Directory.Exists(modsDir))
            return 0;

        foreach (var manifest in Directory.GetFiles(modsDir, "jiangyu.json", SearchOption.AllDirectories))
        {
            var modDir = Path.GetDirectoryName(manifest);
            foreach (var bundlePath in Directory.GetFiles(modDir, "*.bundle"))
            {
                try
                {
                    LoadBundle(bundlePath, log);
                    bundleCount++;
                }
                catch (Exception ex)
                {
                    log.Error($"Failed to load bundle {bundlePath}: {ex.Message}");
                }
            }
        }

        return bundleCount;
    }

    private void LoadBundle(string bundlePath, MelonLogger.Instance log)
    {
        log.Msg($"Loading bundle: {Path.GetFileName(bundlePath)}");

        var manifestPath = Path.Combine(Path.GetDirectoryName(bundlePath), "jiangyu.json");
        var meshMappings = LoadMeshMappings(manifestPath, log);
        var meshMetadata = LoadCompiledMeshMetadata(manifestPath, log);

        var bundle = BundleLoader.LoadFromFile(bundlePath);
        if (bundle == IntPtr.Zero)
        {
            log.Error($"  LoadFromFile returned null for {Path.GetFileName(bundlePath)}");
            return;
        }

        // Build reverse lookup: bundle mesh name → game mesh name
        Dictionary<string, string> bundleToGame = null;
        if (meshMappings != null)
        {
            bundleToGame = new Dictionary<string, string>();
            foreach (var (gameName, bundleName) in meshMappings)
                bundleToGame[bundleName] = gameName;
        }

        // Load prefabs from the bundle
        var gameObjectType = Il2CppType.Of<GameObject>();
        var goTypePtr = IL2CPP.Il2CppObjectBaseToPtr(gameObjectType);
        var meshType = Il2CppType.Of<Mesh>();
        var meshTypePtr = IL2CPP.Il2CppObjectBaseToPtr(meshType);
        var textureType = Il2CppType.Of<Texture2D>();
        var textureTypePtr = IL2CPP.Il2CppObjectBaseToPtr(textureType);
        var assetNames = BundleLoader.GetAllAssetNames(bundle);

        if (assetNames == null || assetNames.Length == 0)
        {
            log.Warning("  Bundle contains no assets.");
            return;
        }

        foreach (var assetName in assetNames)
        {
            var assetPtr = BundleLoader.LoadAsset(bundle, assetName, goTypePtr);
            if (assetPtr != IntPtr.Zero)
            {
                var prefab = new GameObject(assetPtr);
                _pinned.Add(prefab);

                // Instantiate to get a live hierarchy we can read bones from
                var instance = UnityEngine.Object.Instantiate(prefab);
                var keepTemplateInstance = false;
                try
                {
                    instance.hideFlags = HideFlags.HideAndDontSave;

                    var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    if (renderers == null || renderers.Length == 0)
                    {
                        log.Msg($"  {assetName}: no SkinnedMeshRenderers found, skipping");
                        continue;
                    }

                    var mappedTargetNames = new HashSet<string>(StringComparer.Ordinal);

                    for (int r = 0; r < renderers.Length; r++)
                    {
                        var smr = renderers[r];
                        if (smr.sharedMesh == null) continue;

                        var mesh = smr.sharedMesh;
                        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        _pinned.Add(mesh);

                        var bundleMeshName = mesh.name;

                        string targetName;
                        if (bundleToGame != null && bundleToGame.TryGetValue(bundleMeshName, out var mapped))
                            targetName = mapped;
                        else if (bundleToGame != null)
                            continue;
                        else
                            targetName = bundleMeshName;

                        var boneNames = GetBoneNames(smr.bones);
                        string texturePrefix = null;
                        if (meshMetadata != null && meshMetadata.TryGetValue(bundleMeshName, out var prefabMetadata))
                            texturePrefix = prefabMetadata.TexturePrefix;

                        _replacements[targetName] = new ReplacementMesh(mesh, boneNames, texturePrefix);
                        mappedTargetNames.Add(targetName);
                        log.Msg($"  Registered: {bundleMeshName} -> {targetName} ({boneNames.Length} bones, texturePrefix='{texturePrefix ?? "<none>"}')");
                    }

                    var prefabBoneNames = CollectPrefabBoneNames(instance);
                    foreach (var targetName in mappedTargetNames)
                    {
                        _prefabReplacements[targetName] = new ReplacementPrefab(instance, prefab.name, prefabBoneNames);
                    }
                    if (mappedTargetNames.Count > 0)
                    {
                        instance.transform.position = new Vector3(0f, -10000f, 0f);
                        instance.SetActive(false);
                        _pinned.Add(instance);
                        keepTemplateInstance = true;
                        log.Msg($"  Registered prefab: {prefab.name} -> {mappedTargetNames.Count} target mesh name(s) ({prefabBoneNames.Length} bones)");
                    }
                }
                finally
                {
                    if (!keepTemplateInstance)
                        UnityEngine.Object.Destroy(instance);
                }
                continue;
            }

            var texturePtr = BundleLoader.LoadAsset(bundle, assetName, textureTypePtr);
            if (texturePtr != IntPtr.Zero)
            {
                var loadedTexture = new Texture2D(texturePtr);
                loadedTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _replacementTextures[loadedTexture.name] = loadedTexture;
                _pinned.Add(loadedTexture);
                log.Msg($"  Registered texture asset: {loadedTexture.name} ({loadedTexture.width}x{loadedTexture.height})");
                continue;
            }

            if (meshMetadata == null || meshMetadata.Count == 0)
                continue;

            var meshPtr = BundleLoader.LoadAsset(bundle, assetName, meshTypePtr);
            if (meshPtr == IntPtr.Zero)
                continue;

            var loadedMesh = new Mesh(meshPtr);
            if (!meshMetadata.TryGetValue(loadedMesh.name, out var metadataForMesh))
                continue;

            loadedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _pinned.Add(loadedMesh);

            string targetMeshName;
            if (bundleToGame != null && bundleToGame.TryGetValue(loadedMesh.name, out var meshMapped))
                targetMeshName = meshMapped;
            else if (bundleToGame != null)
                continue;
            else
                targetMeshName = loadedMesh.name;

            _replacements[targetMeshName] = new ReplacementMesh(loadedMesh, metadataForMesh.BoneNames, metadataForMesh.TexturePrefix);
            log.Msg($"  Registered mesh asset: {loadedMesh.name} -> {targetMeshName} ({metadataForMesh.BoneNames.Length} bones, texturePrefix='{metadataForMesh.TexturePrefix ?? "<none>"}')");
        }
    }

    private static Dictionary<string, CompiledMeshMetadataRecord> LoadCompiledMeshMetadata(string manifestPath, MelonLogger.Instance log)
    {
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("meshes", out var meshesElement) ||
                meshesElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, CompiledMeshMetadataRecord>(StringComparer.Ordinal);
            foreach (var prop in meshesElement.EnumerateObject())
            {
                var sourceRef = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                    System.Text.Json.JsonValueKind.Object when prop.Value.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == System.Text.Json.JsonValueKind.String => sourceElement.GetString(),
                    _ => null,
                };

                if (string.IsNullOrWhiteSpace(sourceRef))
                    continue;

                var bundleMeshName = GetBundleMeshNameFromSourceRef(sourceRef);
                if (string.IsNullOrWhiteSpace(bundleMeshName))
                    continue;

                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !prop.Value.TryGetProperty("compiled", out var compiledElement) ||
                    compiledElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !compiledElement.TryGetProperty("boneNames", out var boneNamesElement) ||
                    boneNamesElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;

                var boneNames = boneNamesElement.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .ToArray();
                string texturePrefix = null;
                if (compiledElement.TryGetProperty("texturePrefix", out var texturePrefixElement) &&
                    texturePrefixElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    texturePrefix = texturePrefixElement.GetString();
                }

                result[bundleMeshName] = new CompiledMeshMetadataRecord
                {
                    BoneNames = boneNames,
                    TexturePrefix = texturePrefix,
                };
            }

            if (result.Count > 0)
                log.Msg($"  Loaded compiled metadata for {result.Count} mesh asset(s)");
            return result;
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read compiled mesh metadata: {ex.Message}");
        }

        return null;
    }

    private static string GetBundleMeshNameFromSourceRef(string sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
            return null;

        var hashIndex = sourceRef.IndexOf('#');
        return hashIndex >= 0
            ? sourceRef[(hashIndex + 1)..]
            : Path.GetFileNameWithoutExtension(sourceRef);
    }

    private static Dictionary<string, string> LoadMeshMappings(string manifestPath, MelonLogger.Instance log)
    {
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("meshes", out var meshesElement))
                return null;

            var result = new Dictionary<string, string>();
            foreach (var prop in meshesElement.EnumerateObject())
            {
                var gameMeshName = prop.Name;
                var sourceRef = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                    System.Text.Json.JsonValueKind.Object when prop.Value.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == System.Text.Json.JsonValueKind.String => sourceElement.GetString(),
                    _ => null,
                };

                if (string.IsNullOrWhiteSpace(sourceRef))
                    continue;

                var bundleMeshName = GetBundleMeshNameFromSourceRef(sourceRef);

                result[gameMeshName] = bundleMeshName;
            }

            log.Msg($"  Loaded {result.Count} mesh mapping(s) from jiangyu.json");
            return result;
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read jiangyu.json: {ex.Message}");
        }

        return null;
    }

    private Material[] GetOrCreateReplacementMaterials(MelonLogger.Instance log, Material[] sourceMaterials, string texturePrefix)
    {
        if (sourceMaterials == null || sourceMaterials.Length == 0 || string.IsNullOrWhiteSpace(texturePrefix))
            return sourceMaterials;

        var keyParts = new List<string> { texturePrefix, sourceMaterials.Length.ToString() };
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            var material = sourceMaterials[i];
            keyParts.Add(material != null ? material.GetInstanceID().ToString() : "null");
            keyParts.Add(GetTextureName(material, "_BaseColorMap"));
            keyParts.Add(GetTextureName(material, "_MaskMap"));
            keyParts.Add(GetTextureName(material, "_NormalMap"));
            keyParts.Add(GetTextureName(material, "_Effect_Map"));
        }

        var cacheKey = string.Join("|", keyParts);
        if (_materialCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var clones = new Material[sourceMaterials.Length];
        var changedAny = false;
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            var sourceMaterial = sourceMaterials[i];
            if (sourceMaterial == null)
                continue;

            var clone = UnityEngine.Object.Instantiate(sourceMaterial);
            clone.name = $"{sourceMaterial.name} [jiangyu:{texturePrefix}]";
            _pinned.Add(clone);

            var changed = false;
            changed |= TryApplyReplacementTexture(log, clone, "_BaseColorMap", texturePrefix);
            changed |= TryApplyReplacementTexture(log, clone, "_MaskMap", texturePrefix);
            changed |= TryApplyReplacementTexture(log, clone, "_NormalMap", texturePrefix);
            changed |= TryApplyReplacementTexture(log, clone, "_Effect_Map", texturePrefix);
            changedAny |= changed;

            clones[i] = clone;
        }

        _materialCache[cacheKey] = clones;
        return clones;
    }

    private bool TryApplyReplacementTexture(MelonLogger.Instance log, Material material, string propertyName, string texturePrefix)
    {
        if (material == null || string.IsNullOrWhiteSpace(texturePrefix) || !material.HasProperty(propertyName))
            return false;

        var currentTextureName = GetTextureName(material, propertyName);
        var replacementTextureName = ResolveReplacementTextureName(texturePrefix, currentTextureName, propertyName);
        if (string.IsNullOrEmpty(replacementTextureName))
            return false;

        if (!_replacementTextures.TryGetValue(replacementTextureName, out var replacementTexture))
            return false;

        material.SetTexture(propertyName, replacementTexture);
        return true;
    }

    private static string GetTextureName(Material material, string propertyName)
    {
        if (material == null || !material.HasProperty(propertyName))
            return string.Empty;

        return material.GetTexture(propertyName)?.name ?? string.Empty;
    }

    private static string ResolveReplacementTextureName(string texturePrefix, string currentTextureName, string propertyName)
    {
        var mapSuffix = propertyName switch
        {
            "_BaseColorMap" => "BaseMap",
            "_MaskMap" => "MaskMap",
            "_NormalMap" => "NormalMap",
            "_Effect_Map" => "EffectMap",
            _ => null,
        };

        if (mapSuffix == null)
            return null;

        if (!string.IsNullOrWhiteSpace(currentTextureName))
        {
            var variantMatch = System.Text.RegularExpressions.Regex.Match(
                currentTextureName,
                $"^(.*?)(_(\\d+))?_{mapSuffix}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (variantMatch.Success)
            {
                var variantSuffix = variantMatch.Groups[2].Success ? variantMatch.Groups[2].Value : string.Empty;
                return $"{texturePrefix}{variantSuffix}_{mapSuffix}";
            }
        }

        return $"{texturePrefix}_{mapSuffix}";
    }

    /// <summary>
    /// Test: swap basic soldier with conscript soldier using the game's own mesh objects.
    /// Bypasses the entire bundle/import pipeline to isolate whether the swap mechanism works.
    /// </summary>
    /// <summary>
    /// Finds all SkinnedMeshRenderers in the scene and swaps sharedMesh + remaps bones
    /// for any whose mesh name matches a loaded replacement.
    /// </summary>
    public void ApplyReplacements(MelonLogger.Instance log)
    {
        if (_replacements.Count == 0 && _prefabReplacements.Count == 0)
            return;

        var replaced = 0;
        var preparedAssignments = new Dictionary<int, PreparedMeshAssignment>();

        // Scene instances only
        var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        for (int i = 0; i < skinnedRenderers.Count; i++)
        {
            var smr = skinnedRenderers[i].Cast<SkinnedMeshRenderer>();
            if (!IsLiveSceneRenderer(smr)) continue;
            if (IsAlreadyProcessedMesh(smr.sharedMesh)) continue;

            if (_prefabReplacements.TryGetValue(smr.sharedMesh.name, out var prefabReplacement))
            {
                var entityRoot = FindEntityRoot(smr);
                if (entityRoot != null && !_drivenReplacements.ContainsKey(entityRoot.GetInstanceID()))
                {
                    if (TryCreateDrivenReplacement(log, entityRoot, smr, prefabReplacement))
                        replaced++;
                }
                continue;
            }

            if (!_replacements.TryGetValue(smr.sharedMesh.name, out var replacement))
                continue;

            // Remap bones by name
            var gameBones = smr.bones;
            var gameBoneMap = new Dictionary<string, Transform>();
            for (int b = 0; b < gameBones.Length; b++)
                if (gameBones[b] != null)
                    gameBoneMap[gameBones[b].name] = gameBones[b];
            if (smr.rootBone != null)
                CollectBonesRecursive(smr.rootBone.root, gameBoneMap);

            var targetBoneNames = GetBoneNames(gameBones);
            var replacementBoneNames = replacement.BoneNames;
            if ((replacementBoneNames == null || replacementBoneNames.Length == 0) && gameBones.Length > 0)
                replacementBoneNames = targetBoneNames;

            var stripLeadingRoot = ShouldStripLeadingRoot(targetBoneNames, replacementBoneNames);
            var effectiveBoneNames = stripLeadingRoot
                ? replacementBoneNames[1..]
                : replacementBoneNames;

            var newBones = new Transform[effectiveBoneNames.Length];
            for (int b = 0; b < effectiveBoneNames.Length; b++)
                gameBoneMap.TryGetValue(effectiveBoneNames[b], out newBones[b]);

            var newRootBone = smr.rootBone;
            if (!stripLeadingRoot &&
                replacementBoneNames.Length > 0 &&
                !string.IsNullOrEmpty(replacementBoneNames[0]) &&
                gameBoneMap.TryGetValue(replacementBoneNames[0], out var mappedRoot))
            {
                newRootBone = mappedRoot;
            }

            var originalMesh = smr.sharedMesh;
            var originalMeshId = originalMesh.GetInstanceID();
            if (!preparedAssignments.TryGetValue(originalMeshId, out var prepared))
            {
                prepared = new PreparedMeshAssignment
                {
                    UpdateWhenOffscreen = true,
                };

                var replacementHasSkinningMetadata = replacement.BoneNames != null && replacement.BoneNames.Length > 0;
                if (!replacementHasSkinningMetadata && targetMeshVertexCountMatches(originalMesh, replacement.Mesh))
                {
                    var runtimeMesh = InstantiateMeshClone(originalMesh);
                    _pinned.Add(runtimeMesh);

                    if (TryCopyGeometryOntoExistingMesh(log, runtimeMesh, replacement.Mesh))
                    {
                        prepared.Mesh = runtimeMesh;
                        prepared.Bounds = runtimeMesh.bounds;
                        preparedAssignments[originalMeshId] = prepared;
                        log.Msg($"  Prepared runtime mesh clone: {runtimeMesh.name} from {originalMesh.name}");
                    }
                }

                if (prepared.Mesh == null)
                {
                    if (TryUseReplacementMeshDirectly(log, originalMesh, replacement.Mesh, targetBoneNames, effectiveBoneNames))
                    {
                        prepared.Mesh = replacement.Mesh;
                        prepared.Bounds = replacement.Mesh.bounds;
                        preparedAssignments[originalMeshId] = prepared;
                    }
                }

                if (prepared.Mesh == null)
                {
                    if (TryPrepareReplacementMeshForLiveBones(log, smr, originalMesh, replacement.Mesh, newBones, out var preparedReplacementMesh))
                    {
                        _pinned.Add(preparedReplacementMesh);
                        prepared.Mesh = preparedReplacementMesh;
                        prepared.Bounds = preparedReplacementMesh.bounds;
                    }
                    else
                    {
                        prepared.Mesh = replacement.Mesh;
                        prepared.Bounds = replacement.Mesh.bounds;
                    }
                    preparedAssignments[originalMeshId] = prepared;
                }
            }

            // Swap mesh + bones
            smr.sharedMesh = prepared.Mesh;
            smr.bones = new Il2CppReferenceArray<Transform>(newBones);
            smr.rootBone = newRootBone;
            smr.localBounds = prepared.Bounds;
            smr.updateWhenOffscreen = prepared.UpdateWhenOffscreen;

            if (!string.IsNullOrWhiteSpace(replacement.TexturePrefix) &&
                smr.sharedMaterials != null &&
                smr.sharedMaterials.Length > 0 &&
                _replacementTextures.Count > 0)
            {
                smr.sharedMaterials = GetOrCreateReplacementMaterials(log, smr.sharedMaterials, replacement.TexturePrefix);
            }

            replaced++;
            log.Msg($"  Swapped: {smr.sharedMesh.name} (readable={smr.sharedMesh.isReadable}, rootBone={smr.rootBone?.name ?? "<null>"}, boundsCenter={FormatVector3(prepared.Bounds.center)}, boundsSize={FormatVector3(prepared.Bounds.size)}, updateWhenOffscreen={smr.updateWhenOffscreen})");
        }

        if (replaced > 0)
            log.Msg($"Applied {replaced} mesh replacement(s).");
    }

    public bool HasReplacementTargets()
    {
        if (_replacements.Count == 0 && _prefabReplacements.Count == 0)
            return false;

        var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
        for (int i = 0; i < skinnedRenderers.Count; i++)
        {
            var smr = skinnedRenderers[i].Cast<SkinnedMeshRenderer>();
            if (!IsLiveSceneRenderer(smr))
                continue;
            if (IsAlreadyProcessedMesh(smr.sharedMesh))
                continue;

            if (_prefabReplacements.ContainsKey(smr.sharedMesh.name) || _replacements.ContainsKey(smr.sharedMesh.name))
                return true;
        }

        return false;
    }

    private static void CollectBonesRecursive(Transform parent, Dictionary<string, Transform> map)
    {
        if (parent == null) return;
        map.TryAdd(parent.name, parent);
        for (int i = 0; i < parent.childCount; i++)
            CollectBonesRecursive(parent.GetChild(i), map);
    }

    private static bool targetMeshVertexCountMatches(Mesh targetMesh, Mesh replacementMesh)
        => targetMesh != null && replacementMesh != null && targetMesh.vertexCount == replacementMesh.vertexCount;

    private static Dictionary<string, Transform> BuildTransformMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
        CollectBonesRecursive(root, map);
        return map;
    }

    private static Transform FindSkeletonRoot(GameObject rootObject)
    {
        if (rootObject == null)
            return null;

        var smr = rootObject.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (smr?.rootBone != null)
            return smr.rootBone.root;

        return rootObject.transform;
    }

    private static string[] CollectPrefabBoneNames(GameObject prefabInstance)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var renderers = prefabInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in renderers)
        {
            foreach (var bone in smr.bones)
            {
                if (bone != null && !string.IsNullOrEmpty(bone.name))
                    names.Add(bone.name);
            }

            if (smr.rootBone != null && !string.IsNullOrEmpty(smr.rootBone.name))
                names.Add(smr.rootBone.name);
        }

        return names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static void DisableOriginalRenderers(Renderer[] renderers)
    {
        foreach (var renderer in renderers)
        {
            if (renderer != null)
                renderer.enabled = false;
        }
    }

    private static void ConfigureReplacementRenderers(GameObject replacementRoot, SkinnedMeshRenderer sourceSmr, MelonLogger.Instance log)
    {
        if (replacementRoot == null)
            return;

        var lodGroup = replacementRoot.GetComponentInChildren<LODGroup>(true);
        log.Msg($"  [DRIVE] replacement has LODGroup={lodGroup != null}");

        var replacementSmrs = replacementRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        log.Msg($"  [DRIVE] replacement skinned renderer count={replacementSmrs.Length}");
        foreach (var smr in replacementSmrs)
        {
            if (smr == null)
                continue;

            smr.enabled = true;
            smr.updateWhenOffscreen = true;
            smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 10f);

            if (sourceSmr?.sharedMaterials != null && sourceSmr.sharedMaterials.Length > 0)
                smr.sharedMaterials = sourceSmr.sharedMaterials;

            log.Msg($"  [DRIVE] replacement SMR '{smr.name}' mesh='{smr.sharedMesh?.name ?? "<null>"}' mats={smr.sharedMaterials?.Length ?? 0} rootBone='{smr.rootBone?.name ?? "<null>"}' enabled={smr.enabled} active={smr.gameObject.activeInHierarchy} boundsCenter={FormatVector3(smr.localBounds.center)} boundsSize={FormatVector3(smr.localBounds.size)}");
        }

        var renderers = replacementRoot.GetComponentsInChildren<Renderer>(true);
        log.Msg($"  [DRIVE] replacement renderer count={renderers.Length}");
        foreach (var renderer in renderers)
        {
            if (renderer == null)
                continue;

            renderer.enabled = true;
            log.Msg($"  [DRIVE] replacement renderer '{renderer.name}' type={renderer.GetType().Name} enabled={renderer.enabled} active={renderer.gameObject.activeInHierarchy}");
        }
    }

    private static bool IsDrivenReplacementAlive(DrivenReplacement driven)
    {
        if (driven == null)
            return false;

        return driven.EntityRoot != null &&
               driven.ReplacementRoot != null &&
               driven.SourceSkeletonRoot != null &&
               driven.ReplacementSkeletonRoot != null;
    }

    private bool TryCreateDrivenReplacement(MelonLogger.Instance log, GameObject entityRoot, SkinnedMeshRenderer sourceSmr, ReplacementPrefab replacementPrefab)
    {
        if (entityRoot == null || sourceSmr == null || replacementPrefab?.Template == null)
            return false;

        var sourceSkeletonRoot = sourceSmr.rootBone?.root ?? sourceSmr.transform.root;
        if (sourceSkeletonRoot == null)
            return false;

        var hiddenRenderers = entityRoot.GetComponentsInChildren<Renderer>(true);

        var replacementRoot = UnityEngine.Object.Instantiate(replacementPrefab.Template);
        replacementRoot.name = $"{replacementPrefab.PrefabName} [jiangyu-driven]";
        replacementRoot.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _pinned.Add(replacementRoot);
        replacementRoot.SetActive(true);

        replacementRoot.transform.SetParent(entityRoot.transform, false);
        replacementRoot.transform.localPosition = Vector3.zero;
        replacementRoot.transform.localRotation = Quaternion.identity;
        replacementRoot.transform.localScale = Vector3.one;

        ConfigureReplacementRenderers(replacementRoot, sourceSmr, log);

        var replacementAnimator = replacementRoot.GetComponent<Animator>();
        if (replacementAnimator != null)
            replacementAnimator.enabled = false;

        var replacementSkeletonRoot = FindSkeletonRoot(replacementRoot);
        var sourceMap = BuildTransformMap(sourceSkeletonRoot);
        var replacementMap = BuildTransformMap(replacementSkeletonRoot);

        var pairs = new List<TransformPair>();
        var misses = new List<string>();
        foreach (var (name, targetTransform) in replacementMap)
        {
            if (sourceMap.TryGetValue(name, out var sourceTransform))
            {
                pairs.Add(new TransformPair
                {
                    Name = name,
                    Source = sourceTransform,
                    Target = targetTransform,
                });
            }
            else if (misses.Count < 8)
            {
                misses.Add(name);
            }
        }

        if (pairs.Count == 0)
        {
            UnityEngine.Object.Destroy(replacementRoot);
            log.Warning($"  [DRIVE] {entityRoot.name}: no matching bones for prefab '{replacementPrefab.PrefabName}'");
            return false;
        }

        DisableOriginalRenderers(hiddenRenderers);

        var driven = new DrivenReplacement
        {
            EntityRoot = entityRoot,
            ReplacementRoot = replacementRoot,
            SourceSkeletonRoot = sourceSkeletonRoot,
            ReplacementSkeletonRoot = replacementSkeletonRoot,
            BonePairs = pairs.ToArray(),
            HiddenRenderers = hiddenRenderers,
            ReplacementRenderers = replacementRoot.GetComponentsInChildren<Renderer>(true),
            ReplacementSkinnedRenderers = replacementRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true),
            PrefabName = replacementPrefab.PrefabName,
        };

        _drivenReplacements[entityRoot.GetInstanceID()] = driven;

        log.Msg($"  [DRIVE] attached prefab '{replacementPrefab.PrefabName}' to '{entityRoot.name}' hiddenRenderers={hiddenRenderers.Length} mappedBones={pairs.Count}/{replacementMap.Count}");
        log.Msg($"  [DRIVE] source skeleton root='{sourceSkeletonRoot.name}' pos={FormatVector3(sourceSkeletonRoot.position)} scale={FormatVector3(sourceSkeletonRoot.lossyScale)}");
        log.Msg($"  [DRIVE] replacement skeleton root='{replacementSkeletonRoot.name}' pos={FormatVector3(replacementSkeletonRoot.position)} scale={FormatVector3(replacementSkeletonRoot.lossyScale)}");
        if (misses.Count > 0)
            log.Warning($"  [DRIVE] first missing bones: {string.Join(", ", misses)}");

        return true;
    }

    public void UpdateDrivenReplacements(MelonLogger.Instance log)
    {
        if (_drivenReplacements.Count == 0)
            return;

        var staleIds = new List<int>();
        foreach (var (id, driven) in _drivenReplacements)
        {
            if (!IsDrivenReplacementAlive(driven))
            {
                staleIds.Add(id);
                continue;
            }

            foreach (var pair in driven.BonePairs)
            {
                if (pair.Source == null || pair.Target == null)
                    continue;

                pair.Target.position = pair.Source.position;
                pair.Target.rotation = pair.Source.rotation;
                pair.Target.localScale = pair.Source.localScale;
            }

            if (!driven.LoggedInitialSync)
            {
                driven.LoggedInitialSync = true;
                foreach (var sampleName in new[] { "Hips", "Spine", "Head", "Hand_R", "Foot_R" })
                {
                    var sample = driven.BonePairs.FirstOrDefault(p => string.Equals(p.Name, sampleName, StringComparison.Ordinal));
                    if (sample?.Source == null || sample.Target == null)
                        continue;

                    log.Msg($"  [DRIVE] sample {sampleName} sourcePos={FormatVector3(sample.Source.position)} targetPos={FormatVector3(sample.Target.position)}");
                }
            }
        }

        foreach (var staleId in staleIds)
            _drivenReplacements.Remove(staleId);
    }
}
