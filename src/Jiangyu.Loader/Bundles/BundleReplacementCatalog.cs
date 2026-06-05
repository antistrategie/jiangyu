using Il2CppInterop.Runtime;
using Jiangyu.Sdk;
using Jiangyu.Shared.Bundles;
using MelonLoader;
using UnityEngine;
using Jiangyu.Loader.Replacements;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Bundles;

internal sealed class BundleReplacementCatalog
{
    private static readonly Vector3 HiddenTemplateInstancePosition = new(0f, -10000f, 0f);

    private readonly List<UnityEngine.Object> _pinned;
    // The loaded bundle handles per mod folder name (matches ModContext.ModId), so a
    // mod can load its own bundled assets by name through ModContext.Assets.
    private readonly Dictionary<string, List<Il2CppAssetBundle>> _bundlesByMod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IModAssets> _assetsByMod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _meshOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prefabOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textureOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _spriteOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _audioOwners = new(StringComparer.Ordinal);
    // Mirrors AdditionPrefabs' case-insensitive keying so duplicate-detection
    // stays consistent when a later mod ships the same prefab under a
    // different-cased name.
    private readonly Dictionary<string, string> _additionPrefabOwners = new(StringComparer.OrdinalIgnoreCase);

    // Humanoid addition prefabs need their MonoBehaviour config
    // Humanoid addition prefabs need Ragdoll/Footprints field data
    // mirrored from a canonical reference vanilla soldier prefab —
    // AssetRipper strips field values on extract, so the addition
    // reaches the loader with the components attached by bake but no
    // field data. (Hierarchy children like dust spawn markers are
    // mirrored at bake time inside BakeHumanoid, where Editor APIs can
    // mutate the prefab on disk.) The reference lookup needs MENACE's
    // asset registry to be populated, which isn't true during early-boot
    // mod load, so the scheduler queues prefabs and drains during the
    // loader's ApplyReplacements pass.
    public readonly HumanoidMirrorScheduler HumanoidMirror = new();

    public Dictionary<string, ReplacementMesh> Meshes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ReplacementPrefab> Prefabs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Texture2D> ReplacementTextures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Sprite> ReplacementSprites { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AudioClip> ReplacementAudioClips { get; } = new(StringComparer.Ordinal);

    // Mod-shipped addition prefabs declared on jiangyu.json's additionPrefabs
    // list. Looked up by Unity Object.name, satisfying KDL asset= references
    // targeting GameObject-typed fields via ModAssetResolver Phase 1 before
    // the Phase 2 fallback consults the live game-asset registry. Distinct
    // dictionary from Prefabs (which carries ReplacementPrefab metadata for
    // the mesh-replacement path).
    //
    // Case-insensitive on purpose: Unity normalises asset bundle names to
    // lowercase when writing, so a bundle authored as Voymastina/Voymastina
    // lands on disk as voymastina__voymastina.bundle. Modders shouldn't be
    // forced to lowercase their KDL asset= references to compensate.
    public Dictionary<string, GameObject> AdditionPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public BundleReplacementCatalog(List<UnityEngine.Object> pinned)
    {
        _pinned = pinned;
    }

    /// <summary>The mod's own bundled assets, keyed by mod folder name. Mods that
    /// ship no bundles get an empty view. Cached per mod so the name index is built once.</summary>
    public IModAssets AssetsFor(string modFolder, IModHostLog hostLog)
    {
        if (_assetsByMod.TryGetValue(modFolder, out var existing))
            return existing;

        var assets = _bundlesByMod.TryGetValue(modFolder, out var bundles) && bundles.Count > 0
            ? new ModAssetRegistry(modFolder, bundles, _pinned, hostLog)
            : (IModAssets)NullModAssets.Instance;
        _assetsByMod[modFolder] = assets;
        return assets;
    }

    public BundleLoadSummary LoadBundles(ModLoadPlan plan, LoaderLog log)
    {
        var bundleCount = 0;
        var loadableModCount = 0;

        foreach (var blockedMod in plan.BlockedMods)
        {
            log.Error($"Skipping mod '{blockedMod.DisplayName}' [{blockedMod.RelativeDirectoryPath}]: {blockedMod.Reason}");
        }

        foreach (var mod in plan.LoadableMods)
        {
            loadableModCount++;
            log.Mod = mod.Name;
            LogIgnoredConstraints(mod, log);

            if (mod.BundlePaths.Count == 0)
            {
                log.Msg($"No bundle files in [{mod.RelativeDirectoryPath}]; treated as present for dependency checks.");
                continue;
            }

            log.Msg($"Loading from [{mod.RelativeDirectoryPath}]");
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

    private static void LogIgnoredConstraints(DiscoveredMod mod, LoaderLog log)
    {
        foreach (var dependency in mod.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.Constraint))
                continue;

            log.Warning(
                $"Dependency '{dependency.Raw}' includes a version constraint that is not enforced yet; required presence only.");
        }
    }

    private void LoadBundle(DiscoveredMod mod, string bundlePath, LoaderLog log)
    {
        var ownerLabel = $"{mod.Name}/{Path.GetFileName(bundlePath)}";
        log.Debug($"Loading bundle: {Path.GetFileName(bundlePath)}");

        // Parse the mod's jiangyu.json once and reuse the JsonElement across
        // the three readers. LoadBundle runs per bundle, but the manifest is
        // one file per mod, so the previous parse-three-times-per-bundle shape
        // was O(bundles × 3) reads of the same file at startup.
        Dictionary<string, string> meshMappings = null;
        Dictionary<string, CompiledMeshMetadata> meshMetadata = null;
        HashSet<string> additionPrefabNames = new(StringComparer.Ordinal);
        var loaderManifest = OpenManifest(mod.ManifestPath, log);
        if (loaderManifest != null)
        {
            meshMappings = LoadMeshMappings(loaderManifest, log);
            meshMetadata = LoadCompiledMeshMetadata(loaderManifest, log);
            additionPrefabNames = LoadAdditionPrefabNames(loaderManifest);
        }

        // An addition bundle's filename stem is its identity (matches the
        // manifest's additionPrefabs entry, matches the KDL asset= name after
        // ToBundleAssetName translation). Check this once per bundle so the
        // identity doesn't depend on the Unity Object.name inside, which the
        // modder may have set independently of the file layout.
        var bundleStem = Path.GetFileNameWithoutExtension(bundlePath);
        var isAdditionBundle = additionPrefabNames.Contains(bundleStem);

        // Il2CppAssetBundleManager is MelonLoader's hand-resolved AssetBundle
        // wrapper (LavaGang/MelonLoader#1122, shipped in 0.7.3). It bypasses
        // Il2CppInterop's broken byte[]/string marshalling for the AssetBundle
        // ICalls by building a ManagedSpanWrapper from a fixed char* directly,
        // sidestepping ReadOnlySpan<char>.GetPinnableReference (missing on
        // Il2CppInterop 1.5.1). Unlike UnityEngine.AssetBundle (the
        // Il2CppInterop-generated wrapper), this class is safe to call on
        // Unity 6 + Il2CppInterop 1.5.1.
        var bundle = Il2CppAssetBundleManager.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            log.Error($"  LoadFromFile returned null for {Path.GetFileName(bundlePath)}");
            return;
        }

        var modFolder = Path.GetFileName(mod.DirectoryPath);
        if (!_bundlesByMod.TryGetValue(modFolder, out var modBundles))
            _bundlesByMod[modFolder] = modBundles = new List<Il2CppAssetBundle>();
        modBundles.Add(bundle);

        Dictionary<string, string> bundleToGame = null;
        if (meshMappings != null)
        {
            bundleToGame = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (gameName, bundleName) in meshMappings)
                bundleToGame[bundleName] = gameName;
        }

        var goTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<GameObject>());
        var meshTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<Mesh>());
        var textureTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<Texture2D>());
        var spriteTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<Sprite>());
        var audioClipTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<AudioClip>());
        var assetNames = bundle.GetAllAssetNames();

        if (assetNames == null || assetNames.Length == 0)
        {
            log.Warning("  Bundle contains no assets.");
            return;
        }

        foreach (var assetName in assetNames)
        {
            var assetPtr = bundle.LoadAsset(assetName, goTypePtr);
            if (assetPtr != IntPtr.Zero)
            {
                var prefab = new GameObject(assetPtr);
                if (isAdditionBundle)
                {
                    RegisterAdditionPrefab(ownerLabel, prefab, bundleStem, log);
                }
                else
                {
                    RegisterPrefabAsset(ownerLabel, assetName, prefab, bundleToGame, meshMetadata, log);
                }
                continue;
            }

            // Try Sprite before Texture2D: PNG assets imported via Unity's
            // TextureImporter produce a single bundle asset where both types
            // are loadable from the same name (Texture2D is the main asset
            // and Sprite is a sub-asset). Loading Texture2D first would hide
            // the sprite from the catalog and break KDL asset= references
            // typed against Sprite. Pure texture replacements (no sprite
            // sub-asset) return null here and fall through cleanly.
            var spritePtr = bundle.LoadAsset(assetName, spriteTypePtr);
            if (spritePtr != IntPtr.Zero)
            {
                RegisterSpriteAsset(ownerLabel, spritePtr, log);
                continue;
            }

            var texturePtr = bundle.LoadAsset(assetName, textureTypePtr);
            if (texturePtr != IntPtr.Zero)
            {
                RegisterTextureAsset(ownerLabel, texturePtr, log);
                continue;
            }

            var audioClipPtr = bundle.LoadAsset(assetName, audioClipTypePtr);
            if (audioClipPtr != IntPtr.Zero)
            {
                RegisterAudioAsset(ownerLabel, audioClipPtr, log);
                continue;
            }

            if (meshMetadata == null || meshMetadata.Count == 0)
                continue;

            var meshPtr = bundle.LoadAsset(assetName, meshTypePtr);
            if (meshPtr != IntPtr.Zero)
                RegisterMeshAsset(ownerLabel, meshPtr, bundleToGame, meshMetadata, log);
        }
    }

    private void RegisterPrefabAsset(
        string ownerLabel,
        string assetName,
        GameObject prefab,
        Dictionary<string, string> bundleToGame,
        Dictionary<string, CompiledMeshMetadata> meshMetadata,
        LoaderLog log)
    {
        _pinned.Add(prefab);

        var instance = UnityEngine.Object.Instantiate(prefab);
        var keepTemplateInstance = false;
        try
        {
            instance.hideFlags = HideFlags.HideAndDontSave;

            var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                log.Debug($"  {assetName}: no SkinnedMeshRenderers found, skipping");
                return;
            }

            var mappedTargetKeys = new HashSet<string>(StringComparer.Ordinal);
            var metadataByTargetKey = new Dictionary<string, CompiledMeshMetadata>(StringComparer.Ordinal);
            foreach (var smr in renderers)
            {
                if (smr.sharedMesh == null)
                    continue;

                var mesh = smr.sharedMesh;
                mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _pinned.Add(mesh);

                var bundleMeshName = mesh.name;
                var targetKey = ResolveTargetMeshName(bundleMeshName, bundleToGame);
                if (targetKey == null)
                    continue;

                IReadOnlyList<CompiledMaterialBinding> materialBindings = Array.Empty<CompiledMaterialBinding>();
                var targetRendererPath = targetKey;
                string targetEntityName = null;
                if (meshMetadata != null && meshMetadata.TryGetValue(bundleMeshName, out var prefabMetadata))
                {
                    materialBindings = (IReadOnlyList<CompiledMaterialBinding>)prefabMetadata.Materials ?? Array.Empty<CompiledMaterialBinding>();
                    targetRendererPath = string.IsNullOrWhiteSpace(prefabMetadata.TargetRendererPath)
                        ? targetKey
                        : prefabMetadata.TargetRendererPath;
                    targetEntityName = prefabMetadata.TargetEntityName;
                    metadataByTargetKey[targetKey] = prefabMetadata;
                }

                RegisterMeshOverride(
                    targetKey,
                    new ReplacementMesh(mesh, GetBoneNames(smr.bones), materialBindings, targetRendererPath, targetEntityName),
                    ownerLabel,
                    log);
                mappedTargetKeys.Add(targetKey);
                log.Debug($"  Registered: {bundleMeshName} -> {targetKey} ({smr.bones.Length} bones, materialBindings={materialBindings.Count})");
            }

            if (mappedTargetKeys.Count == 0)
                return;

            var prefabBoneNames = CollectPrefabBoneNames(instance);
            foreach (var targetKey in mappedTargetKeys)
            {
                var targetRendererPath = targetKey;
                string targetEntityName = null;
                if (metadataByTargetKey.TryGetValue(targetKey, out var prefabMetadata))
                {
                    targetRendererPath = string.IsNullOrWhiteSpace(prefabMetadata.TargetRendererPath)
                        ? targetKey
                        : prefabMetadata.TargetRendererPath;
                    targetEntityName = prefabMetadata.TargetEntityName;
                }

                RegisterPrefabOverride(
                    targetKey,
                    new ReplacementPrefab(instance, prefab.name, prefabBoneNames, targetRendererPath, targetEntityName),
                    ownerLabel,
                    log);
            }

            instance.transform.position = HiddenTemplateInstancePosition;
            instance.SetActive(false);
            _pinned.Add(instance);
            keepTemplateInstance = true;
            log.Debug($"  Registered prefab: {prefab.name} -> {mappedTargetKeys.Count} target renderer path(s) ({prefabBoneNames.Length} bones)");
        }
        finally
        {
            if (!keepTemplateInstance)
                UnityEngine.Object.Destroy(instance);
        }
    }

    private void RegisterTextureAsset(string ownerLabel, IntPtr texturePtr, LoaderLog log)
    {
        var loadedTexture = new Texture2D(texturePtr)
        {
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        RegisterTextureOverride(loadedTexture.name, loadedTexture, ownerLabel, log);
        _pinned.Add(loadedTexture);
        log.Debug($"  Registered texture asset: {loadedTexture.name} ({loadedTexture.width}x{loadedTexture.height})");
    }

    private void RegisterAudioAsset(string ownerLabel, IntPtr audioClipPtr, LoaderLog log)
    {
        var loadedClip = new AudioClip(audioClipPtr)
        {
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        RegisterAudioOverride(loadedClip.name, loadedClip, ownerLabel, log);
        _pinned.Add(loadedClip);
        log.Debug($"  Registered audio asset: {loadedClip.name}");
    }

    private void RegisterSpriteAsset(string ownerLabel, IntPtr spritePtr, LoaderLog log)
    {
        var loadedSprite = new Sprite(spritePtr)
        {
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        RegisterSpriteOverride(loadedSprite.name, loadedSprite, ownerLabel, log);
        _pinned.Add(loadedSprite);
        log.Debug($"  Registered sprite asset: {loadedSprite.name}");

        // JIANGYU-CONTRACT: Sprite replacement lands via in-place mutation of
        // the backing Texture2D. Registering the bundle sprite's backing texture
        // under the sprite's name lets TextureMutationService find it during
        // its sweep (game Sprites carry the same .name as their backing texture
        // for the unique-texture-backed case, which compile-time validation
        // ensures is the only case we accept). Explicit texture replacements
        // take precedence if both are registered under the same name.
        //
        // The .texture cast can throw if the bundle's sprite was built through
        // a path that produced an unresolvable m_RD.texture PPtr (older
        // runtime-Texture2D pipeline). Skip the backing-texture registration
        // for those sprites rather than aborting the whole bundle load.
        Texture2D backingTexture;
        try
        {
            backingTexture = loadedSprite.texture;
        }
        catch (Exception ex)
        {
            log.Warning($"    Sprite '{loadedSprite.name}': backing texture access failed ({ex.GetType().Name}); skipping backing-texture registration.");
            return;
        }
        if (backingTexture != null && !ReplacementTextures.ContainsKey(loadedSprite.name))
        {
            backingTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            RegisterTextureOverride(loadedSprite.name, backingTexture, ownerLabel + " (sprite backing)", log);
            _pinned.Add(backingTexture);
        }
    }

    // Register a GameObject from a modder-shipped addition bundle. Skips the
    // mesh-replacement processing in RegisterPrefabAsset entirely; the prefab
    // is held under the bundle's filename stem (matches the KDL asset=
    // reference after ToBundleAssetName translation) for ModAssetResolver
    // Phase 1 lookups. Object.name on the GameObject is irrelevant here so
    // modders can name prefabs whatever they want inside Unity without
    // affecting the lookup contract.
    private void RegisterAdditionPrefab(string ownerLabel, GameObject prefab, string key, LoaderLog log)
    {
        prefab.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _pinned.Add(prefab);

        // Rebind shaders to the runtime's resolved shader by name.
        // AssetRipper extracts shaders as stubs (real HLSL isn't recoverable
        // from compiled bytecode), so bundled materials would otherwise fall
        // back to Hidden/InternalErrorShader (magenta) at render time. The
        // runtime has the real shaders loaded for the game's own assets, so
        // Shader.Find(name) resolves to a working shader of the same name.
        // Material properties (colors, textures, keywords) are preserved
        // because we only swap the shader pointer.
        //
        // Materials whose shader name doesn't resolve at runtime (e.g.
        // "Universal Render Pipeline/Lit" — MENACE ships its own Menace/*
        // family and not the URP package's stock shaders) are left on their
        // broken stub shader so the magenta render in-game makes the
        // mis-authoring visible.
        var rebinds = 0;
        var unresolved = 0;
        foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                if (mat == null || mat.shader == null) continue;
                var name = mat.shader.name;
                if (string.IsNullOrEmpty(name)) continue;
                var runtimeShader = Shader.Find(name);
                if (runtimeShader == null)
                {
                    log.Warning($"    Material '{mat.name}' references shader '{name}' but Shader.Find returned null; will render magenta in-game. Use a Menace/* shader instead.");
                    unresolved++;
                    continue;
                }
                mat.shader = runtimeShader;
                rebinds++;
            }
            renderer.sharedMaterials = mats;
        }

        var soldierScripts = HumanoidMirror.Queue(prefab, log.Raw);

        if (_additionPrefabOwners.TryGetValue(key, out var previousOwner))
            log.Warning($"  Override addition prefab '{key}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        AdditionPrefabs[key] = prefab;
        _additionPrefabOwners[key] = ownerLabel;
        var shaderSuffix = unresolved > 0
            ? $"; rebound {rebinds} shader(s); {unresolved} unresolved (will render magenta)"
            : $"; rebound {rebinds} shader(s)";
        log.Debug($"  Registered addition prefab: {key} (object name: {prefab.name}{shaderSuffix}{soldierScripts})");
    }

    private void RegisterMeshAsset(
        string ownerLabel,
        IntPtr meshPtr,
        Dictionary<string, string> bundleToGame,
        Dictionary<string, CompiledMeshMetadata> meshMetadata,
        LoaderLog log)
    {
        var loadedMesh = new Mesh(meshPtr);
        if (!meshMetadata.TryGetValue(loadedMesh.name, out var metadataForMesh))
            return;

        loadedMesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _pinned.Add(loadedMesh);

        var targetKey = ResolveTargetMeshName(loadedMesh.name, bundleToGame);
        if (targetKey == null)
            return;

        var targetRendererPath = string.IsNullOrWhiteSpace(metadataForMesh.TargetRendererPath)
            ? targetKey
            : metadataForMesh.TargetRendererPath;
        RegisterMeshOverride(
            targetKey,
            new ReplacementMesh(
                loadedMesh,
                metadataForMesh.BoneNames,
                (IReadOnlyList<CompiledMaterialBinding>)metadataForMesh.Materials ?? Array.Empty<CompiledMaterialBinding>(),
                targetRendererPath,
                metadataForMesh.TargetEntityName),
            ownerLabel,
            log);
        log.Debug($"  Registered mesh asset: {loadedMesh.name} -> {targetKey} ({metadataForMesh.BoneNames.Length} bones, materialBindings={metadataForMesh.Materials?.Count ?? 0})");
    }

    private void RegisterMeshOverride(string targetName, ReplacementMesh mesh, string ownerLabel, LoaderLog log)
    {
        if (_meshOwners.TryGetValue(targetName, out var previousOwner))
            log.Warning($"  Override mesh target '{targetName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        Meshes[targetName] = mesh;
        _meshOwners[targetName] = ownerLabel;
    }

    private void RegisterPrefabOverride(string targetName, ReplacementPrefab prefab, string ownerLabel, LoaderLog log)
    {
        if (_prefabOwners.TryGetValue(targetName, out var previousOwner))
            log.Warning($"  Override prefab target '{targetName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        Prefabs[targetName] = prefab;
        _prefabOwners[targetName] = ownerLabel;
    }

    private void RegisterTextureOverride(string textureName, Texture2D texture, string ownerLabel, LoaderLog log)
    {
        if (_textureOwners.TryGetValue(textureName, out var previousOwner))
            log.Warning($"  Override texture '{textureName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        ReplacementTextures[textureName] = texture;
        _textureOwners[textureName] = ownerLabel;
    }

    private void RegisterSpriteOverride(string spriteName, Sprite sprite, string ownerLabel, LoaderLog log)
    {
        if (_spriteOwners.TryGetValue(spriteName, out var previousOwner))
            log.Warning($"  Override sprite '{spriteName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        ReplacementSprites[spriteName] = sprite;
        _spriteOwners[spriteName] = ownerLabel;
    }

    private void RegisterAudioOverride(string clipName, AudioClip clip, string ownerLabel, LoaderLog log)
    {
        if (_audioOwners.TryGetValue(clipName, out var previousOwner))
            log.Warning($"  Override audio '{clipName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        ReplacementAudioClips[clipName] = clip;
        _audioOwners[clipName] = ownerLabel;
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

    private static LoaderManifest OpenManifest(string manifestPath, LoaderLog log)
    {
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            return LoaderManifest.FromJson(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read jiangyu.json: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, CompiledMeshMetadata> LoadCompiledMeshMetadata(LoaderManifest manifest, LoaderLog log)
    {
        if (manifest.Meshes == null || manifest.Meshes.Count == 0)
            return null;

        var result = new Dictionary<string, CompiledMeshMetadata>(StringComparer.Ordinal);
        foreach (var (_, entry) in manifest.Meshes)
        {
            if (entry?.Compiled == null)
                continue;

            var bundleMeshName = GetBundleMeshNameFromSourceRef(entry.Source);
            if (string.IsNullOrWhiteSpace(bundleMeshName))
                continue;

            // Defensive: filter invalid material entries and sort by slot so
            // the apply loop sees a predictable order. Compile already emits
            // them sorted, but a hand-edited compiled manifest could disagree.
            if (entry.Compiled.Materials != null)
            {
                entry.Compiled.Materials = entry.Compiled.Materials
                    .Where(b => b?.Textures != null && b.Textures.Count > 0)
                    .OrderBy(b => b.Slot)
                    .ToList();
            }

            result[bundleMeshName] = entry.Compiled;
        }

        if (result.Count > 0)
            log.Msg($"  Loaded compiled metadata for {result.Count} mesh asset(s)");

        return result;
    }

    private static HashSet<string> LoadAdditionPrefabNames(LoaderManifest manifest)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.AdditionPrefabs == null)
            return set;

        foreach (var name in manifest.AdditionPrefabs)
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name);
        }
        return set;
    }

    private static Dictionary<string, string> LoadMeshMappings(LoaderManifest manifest, LoaderLog log)
    {
        if (manifest.Meshes == null)
            return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, entry) in manifest.Meshes)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Source))
                continue;
            result[path] = GetBundleMeshNameFromSourceRef(entry.Source);
        }

        log.Msg($"  Loaded {result.Count} mesh mapping(s) from jiangyu.json");
        return result;
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
