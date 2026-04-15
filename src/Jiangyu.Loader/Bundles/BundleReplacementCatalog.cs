using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using Jiangyu.Loader.Replacements;

namespace Jiangyu.Loader.Bundles;

internal sealed class BundleReplacementCatalog
{
    private static readonly Vector3 HiddenTemplateInstancePosition = new(0f, -10000f, 0f);

    private readonly List<UnityEngine.Object> _pinned;
    private readonly Dictionary<string, string> _meshOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prefabOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textureOwners = new(StringComparer.Ordinal);

    public Dictionary<string, ReplacementMesh> Meshes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ReplacementPrefab> Prefabs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Texture2D> ReplacementTextures { get; } = new(StringComparer.Ordinal);

    public BundleReplacementCatalog(List<UnityEngine.Object> pinned)
    {
        _pinned = pinned;
    }

    public BundleLoadSummary LoadBundles(string modsDir, MelonLogger.Instance log)
    {
        var bundleCount = 0;
        var loadableModCount = 0;

        if (!Directory.Exists(modsDir))
            return new BundleLoadSummary(0, 0, 0);

        var plan = ModLoadPlanBuilder.Build(modsDir);
        foreach (var blockedMod in plan.BlockedMods)
        {
            log.Error($"Skipping mod '{blockedMod.DisplayName}' [{blockedMod.RelativeDirectoryPath}]: {blockedMod.Reason}");
        }

        foreach (var mod in plan.LoadableMods)
        {
            loadableModCount++;
            LogIgnoredConstraints(mod, log);

            if (mod.BundlePaths.Count == 0)
            {
                log.Msg($"Mod '{mod.Name}' [{mod.RelativeDirectoryPath}] has no bundle files; treated as present for dependency checks.");
                continue;
            }

            log.Msg($"Loading mod '{mod.Name}' from [{mod.RelativeDirectoryPath}]");
            foreach (var bundlePath in mod.BundlePaths)
            {
                try
                {
                    LoadBundle(mod, bundlePath, log);
                    bundleCount++;
                }
                catch (Exception ex)
                {
                    log.Error($"Failed to load bundle {bundlePath}: {ex.Message}");
                }
            }
        }

        return new BundleLoadSummary(loadableModCount, plan.BlockedMods.Count, bundleCount);
    }

    private static void LogIgnoredConstraints(DiscoveredMod mod, MelonLogger.Instance log)
    {
        foreach (var dependency in mod.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.Constraint))
                continue;

            log.Warning(
                $"Mod '{mod.Name}' dependency '{dependency.Raw}' includes a version constraint that is not enforced yet; required presence only.");
        }
    }

    private void LoadBundle(DiscoveredMod mod, string bundlePath, MelonLogger.Instance log)
    {
        var ownerLabel = $"{mod.Name}/{Path.GetFileName(bundlePath)}";
        log.Msg($"Loading bundle: {Path.GetFileName(bundlePath)}");

        var manifestPath = mod.ManifestPath;
        var meshMappings = LoadMeshMappings(manifestPath, log);
        var meshMetadata = LoadCompiledMeshMetadata(manifestPath, log);

        var bundle = BundleLoader.LoadFromFile(bundlePath);
        if (bundle == IntPtr.Zero)
        {
            log.Error($"  LoadFromFile returned null for {Path.GetFileName(bundlePath)}");
            return;
        }

        Dictionary<string, string> bundleToGame = null;
        if (meshMappings != null)
        {
            bundleToGame = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (gameName, bundleName) in meshMappings)
                bundleToGame[bundleName] = gameName;
        }

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
                RegisterPrefabAsset(ownerLabel, assetName, assetPtr, bundleToGame, meshMetadata, log);
                continue;
            }

            var texturePtr = BundleLoader.LoadAsset(bundle, assetName, textureTypePtr);
            if (texturePtr != IntPtr.Zero)
            {
                RegisterTextureAsset(ownerLabel, texturePtr, log);
                continue;
            }

            if (meshMetadata == null || meshMetadata.Count == 0)
                continue;

            var meshPtr = BundleLoader.LoadAsset(bundle, assetName, meshTypePtr);
            if (meshPtr != IntPtr.Zero)
                RegisterMeshAsset(ownerLabel, meshPtr, bundleToGame, meshMetadata, log);
        }
    }

    private void RegisterPrefabAsset(
        string ownerLabel,
        string assetName,
        IntPtr assetPtr,
        Dictionary<string, string> bundleToGame,
        Dictionary<string, CompiledMeshMetadataRecord> meshMetadata,
        MelonLogger.Instance log)
    {
        var prefab = new GameObject(assetPtr);
        _pinned.Add(prefab);

        var instance = UnityEngine.Object.Instantiate(prefab);
        var keepTemplateInstance = false;
        try
        {
            instance.hideFlags = HideFlags.HideAndDontSave;

            var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                log.Msg($"  {assetName}: no SkinnedMeshRenderers found, skipping");
                return;
            }

            var mappedTargetNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in renderers)
            {
                if (smr.sharedMesh == null)
                    continue;

                var mesh = smr.sharedMesh;
                mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _pinned.Add(mesh);

                var bundleMeshName = mesh.name;
                var targetName = ResolveTargetMeshName(bundleMeshName, bundleToGame);
                if (targetName == null)
                    continue;

                var materialBindings = Array.Empty<CompiledMaterialBindingRecord>();
                if (meshMetadata != null && meshMetadata.TryGetValue(bundleMeshName, out var prefabMetadata))
                    materialBindings = prefabMetadata.MaterialBindings ?? Array.Empty<CompiledMaterialBindingRecord>();

                RegisterMeshOverride(
                    targetName,
                    new ReplacementMesh(mesh, GetBoneNames(smr.bones), materialBindings),
                    ownerLabel,
                    log);
                mappedTargetNames.Add(targetName);
                log.Msg($"  Registered: {bundleMeshName} -> {targetName} ({smr.bones.Length} bones, materialBindings={materialBindings.Length})");
            }

            if (mappedTargetNames.Count == 0)
                return;

            var prefabBoneNames = CollectPrefabBoneNames(instance);
            foreach (var targetName in mappedTargetNames)
                RegisterPrefabOverride(
                    targetName,
                    new ReplacementPrefab(instance, prefab.name, prefabBoneNames),
                    ownerLabel,
                    log);

            instance.transform.position = HiddenTemplateInstancePosition;
            instance.SetActive(false);
            _pinned.Add(instance);
            keepTemplateInstance = true;
            log.Msg($"  Registered prefab: {prefab.name} -> {mappedTargetNames.Count} target mesh name(s) ({prefabBoneNames.Length} bones)");
        }
        finally
        {
            if (!keepTemplateInstance)
                UnityEngine.Object.Destroy(instance);
        }
    }

    private void RegisterTextureAsset(string ownerLabel, IntPtr texturePtr, MelonLogger.Instance log)
    {
        var loadedTexture = new Texture2D(texturePtr)
        {
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        RegisterTextureOverride(loadedTexture.name, loadedTexture, ownerLabel, log);
        _pinned.Add(loadedTexture);
        log.Msg($"  Registered texture asset: {loadedTexture.name} ({loadedTexture.width}x{loadedTexture.height})");
    }

    private void RegisterMeshAsset(
        string ownerLabel,
        IntPtr meshPtr,
        Dictionary<string, string> bundleToGame,
        Dictionary<string, CompiledMeshMetadataRecord> meshMetadata,
        MelonLogger.Instance log)
    {
        var loadedMesh = new Mesh(meshPtr);
        if (!meshMetadata.TryGetValue(loadedMesh.name, out var metadataForMesh))
            return;

        loadedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _pinned.Add(loadedMesh);

        var targetMeshName = ResolveTargetMeshName(loadedMesh.name, bundleToGame);
        if (targetMeshName == null)
            return;

        RegisterMeshOverride(
            targetMeshName,
            new ReplacementMesh(loadedMesh, metadataForMesh.BoneNames, metadataForMesh.MaterialBindings ?? Array.Empty<CompiledMaterialBindingRecord>()),
            ownerLabel,
            log);
        log.Msg($"  Registered mesh asset: {loadedMesh.name} -> {targetMeshName} ({metadataForMesh.BoneNames.Length} bones, materialBindings={metadataForMesh.MaterialBindings?.Length ?? 0})");
    }

    private void RegisterMeshOverride(string targetName, ReplacementMesh mesh, string ownerLabel, MelonLogger.Instance log)
    {
        if (_meshOwners.TryGetValue(targetName, out var previousOwner))
            log.Warning($"  Override mesh target '{targetName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        Meshes[targetName] = mesh;
        _meshOwners[targetName] = ownerLabel;
    }

    private void RegisterPrefabOverride(string targetName, ReplacementPrefab prefab, string ownerLabel, MelonLogger.Instance log)
    {
        if (_prefabOwners.TryGetValue(targetName, out var previousOwner))
            log.Warning($"  Override prefab target '{targetName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        Prefabs[targetName] = prefab;
        _prefabOwners[targetName] = ownerLabel;
    }

    private void RegisterTextureOverride(string textureName, Texture2D texture, string ownerLabel, MelonLogger.Instance log)
    {
        if (_textureOwners.TryGetValue(textureName, out var previousOwner))
            log.Warning($"  Override texture '{textureName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        ReplacementTextures[textureName] = texture;
        _textureOwners[textureName] = ownerLabel;
    }

    private static string ResolveTargetMeshName(string bundleMeshName, Dictionary<string, string> bundleToGame)
    {
        if (bundleToGame == null)
            return bundleMeshName;

        return bundleToGame.TryGetValue(bundleMeshName, out var mapped)
            ? mapped
            : null;
    }

    private static string[] GetBoneNames(Transform[] bones)
    {
        var names = new string[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            names[i] = bones[i]?.name ?? string.Empty;
        return names;
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
            {
                return null;
            }

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
                {
                    continue;
                }

                var boneNames = boneNamesElement.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .ToArray();

                result[bundleMeshName] = new CompiledMeshMetadataRecord
                {
                    BoneNames = boneNames,
                    MaterialBindings = LoadCompiledMaterialBindings(compiledElement),
                };
            }

            if (result.Count > 0)
                log.Msg($"  Loaded compiled metadata for {result.Count} mesh asset(s)");

            return result;
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read compiled mesh metadata: {ex.Message}");
            return null;
        }
    }

    private static CompiledMaterialBindingRecord[] LoadCompiledMaterialBindings(System.Text.Json.JsonElement compiledElement)
    {
        if (!compiledElement.TryGetProperty("materials", out var materialsElement) ||
            materialsElement.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return Array.Empty<CompiledMaterialBindingRecord>();
        }

        var result = new List<CompiledMaterialBindingRecord>();
        foreach (var materialElement in materialsElement.EnumerateArray())
        {
            if (materialElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !materialElement.TryGetProperty("slot", out var slotElement) ||
                slotElement.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !slotElement.TryGetInt32(out var slot) ||
                !materialElement.TryGetProperty("textures", out var texturesElement) ||
                texturesElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var textures = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var textureProperty in texturesElement.EnumerateObject())
            {
                if (textureProperty.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;

                var textureName = textureProperty.Value.GetString();
                if (string.IsNullOrWhiteSpace(textureName))
                    continue;

                textures[textureProperty.Name] = textureName;
            }

            if (textures.Count == 0)
                continue;

            result.Add(new CompiledMaterialBindingRecord
            {
                Slot = slot,
                Name = materialElement.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? nameElement.GetString()
                    : null,
                Textures = textures,
            });
        }

        return result.OrderBy(binding => binding.Slot).ToArray();
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

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
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

                result[prop.Name] = GetBundleMeshNameFromSourceRef(sourceRef);
            }

            log.Msg($"  Loaded {result.Count} mesh mapping(s) from jiangyu.json");
            return result;
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read jiangyu.json: {ex.Message}");
            return null;
        }
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
}

internal readonly record struct BundleLoadSummary(int LoadableModCount, int BlockedModCount, int LoadedBundleCount);
