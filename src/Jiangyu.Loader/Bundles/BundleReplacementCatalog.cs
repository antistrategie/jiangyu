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
    private readonly List<UnityEngine.Object> _pinned;
    // The loaded bundle handles per mod id (the manifest name, matching ModContext.ModId),
    // so a mod can load its own bundled assets by name through ModContext.Assets. Keyed on
    // the id and never the folder name: a mod's directory can be named anything, and a
    // player who prefixes it to order the load ("000-WOMENACE") would otherwise file the
    // bundles under a key nothing looks them up by, leaving the mod unable to find its own
    // assets while everything global about it still worked.
    private readonly Dictionary<string, List<Il2CppAssetBundle>> _bundlesByMod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IModAssets> _assetsByMod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _meshOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textureOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _spriteOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _audioOwners = new(StringComparer.Ordinal);
    // Mirrors AdditionPrefabs' case-insensitive keying so duplicate-detection
    // stays consistent when a later mod ships the same prefab under a
    // different-cased name.
    private readonly Dictionary<string, string> _additionPrefabOwners = new(StringComparer.OrdinalIgnoreCase);

    // Addition prefabs that declare a vanilla reference need MENACE
    // MonoBehaviours a modder's Unity project cannot author: Ragdoll and
    // Footprints field data on a soldier-shape addition, and the whole
    // script set on a vanilla sub-assembly copied into any prefab.
    // (Hierarchy children like dust spawn markers are mirrored at bake
    // time inside BakeHumanoid, where Editor APIs can mutate the prefab
    // on disk.) The reference lookup needs MENACE's asset registry to be
    // populated, which isn't true during early-boot mod load, so the
    // scheduler queues prefabs and drains during the loader's
    // ApplyReplacements pass.
    public readonly PrefabMirrorScheduler PrefabMirrors = new();

    public Dictionary<string, ReplacementMesh> Meshes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Texture2D> ReplacementTextures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Sprite> ReplacementSprites { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AudioClip> ReplacementAudioClips { get; } = new(StringComparer.Ordinal);

    // Mod-shipped addition prefabs declared on jiangyu.json's additionPrefabs
    // list. Looked up by Unity Object.name, satisfying KDL asset= references
    // targeting GameObject-typed fields via ModAssetResolver Phase 1 before
    // the Phase 2 fallback consults the live game-asset registry.
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

    /// <summary>The mod's own bundled assets, keyed by mod id. Mods that ship no
    /// bundles get an empty view. Cached per mod so the name index is built once.</summary>
    public int BundleCountFor(string modId)
        => _bundlesByMod.TryGetValue(modId, out var bundles) ? bundles.Count : 0;

    public IModAssets AssetsFor(string modId, IModHostLog hostLog)
    {
        if (_assetsByMod.TryGetValue(modId, out var existing))
            return existing;

        var assets = _bundlesByMod.TryGetValue(modId, out var bundles) && bundles.Count > 0
            ? new ModAssetRegistry(modId, bundles, _pinned, hostLog)
            : (IModAssets)NullModAssets.Instance;
        _assetsByMod[modId] = assets;
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

            if (mod.BundlePaths.Count == 0)
            {
                log.Debug($"No bundle files in [{mod.RelativeDirectoryPath}]; treated as present for dependency checks.");
                continue;
            }

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

        if (!_bundlesByMod.TryGetValue(mod.Name, out var modBundles))
            _bundlesByMod[mod.Name] = modBundles = new List<Il2CppAssetBundle>();
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
            var registered = false;
            foreach (var kind in ProbeOrderFor(assetName))
            {
                // Each LoadAsset miss is a native bundle lookup that costs the
                // same as a hit, so the asset's own extension picks which type
                // to try first. Every kind is still attempted, and the first
                // type that loads is the one the asset registers as.
                var typePtr = kind switch
                {
                    AssetProbeKind.GameObject => goTypePtr,
                    AssetProbeKind.Sprite => spriteTypePtr,
                    AssetProbeKind.Texture => textureTypePtr,
                    _ => audioClipTypePtr,
                };

                var ptr = bundle.LoadAsset(assetName, typePtr);
                if (ptr == IntPtr.Zero)
                    continue;

                switch (kind)
                {
                    case AssetProbeKind.GameObject:
                        var prefab = new GameObject(ptr);
                        if (isAdditionBundle)
                            RegisterAdditionPrefab(ownerLabel, prefab, bundleStem, log);
                        else
                            RegisterPrefabAsset(ownerLabel, assetName, prefab, bundleToGame, meshMetadata, log);
                        break;
                    case AssetProbeKind.Sprite:
                        RegisterSpriteAsset(ownerLabel, ptr, log);
                        break;
                    case AssetProbeKind.Texture:
                        RegisterTextureAsset(ownerLabel, ptr, log);
                        break;
                    default:
                        RegisterAudioAsset(ownerLabel, ptr, log);
                        break;
                }

                registered = true;
                break;
            }

            if (registered)
                continue;

            if (meshMetadata == null || meshMetadata.Count == 0)
                continue;

            var meshPtr = bundle.LoadAsset(assetName, meshTypePtr);
            if (meshPtr != IntPtr.Zero)
                RegisterMeshAsset(ownerLabel, meshPtr, bundleToGame, meshMetadata, log);
        }
    }

    internal enum AssetProbeKind { GameObject, Sprite, Texture, Audio }

    internal enum ShaderRebindAction
    {
        /// <summary>The runtime owns a shader of this name. Point the material at it.</summary>
        Rebind,
        /// <summary>The shader travelled in the mod's own bundle. Leave the material on it.</summary>
        KeepModShipped,
        /// <summary>An extraction stub for a shader the runtime does not own. Renders wrong.</summary>
        BrokenStub,
    }

    // Shader namespaces that only ever reach a mod bundle by extraction: the
    // engine's own families plus MENACE's. A modder authoring a shader picks
    // their own namespace, so a name in one of these that the runtime cannot
    // resolve is a stale or foreign stub rather than shipped work.
    private static readonly string[] ExtractedShaderNamespaces =
        { "Hidden/", "HDRP/", "Menace/", "Shader Graphs/", "Universal Render Pipeline/" };

    /// <summary>
    /// Decide what to do with one bundled material's shader reference.
    /// <paramref name="resolvedAtRuntime"/> is whether <c>Shader.Find</c> found a
    /// shader of this name, which is the test for "the game owns this shader".
    /// </summary>
    internal static ShaderRebindAction ClassifyShader(string shaderName, bool resolvedAtRuntime)
    {
        // A material whose serialized shader reference dangled at bake time (its
        // stub's GUID no longer existed in the mod's Unity project) loads on the
        // builtin error shader. Shader.Find resolves that name, so the check for
        // it has to come before the resolved case or it counts as a rebind while
        // the material renders magenta in-game.
        if (shaderName == "Hidden/InternalErrorShader")
            return ShaderRebindAction.BrokenStub;

        if (resolvedAtRuntime)
            return ShaderRebindAction.Rebind;

        foreach (var prefix in ExtractedShaderNamespaces)
        {
            if (shaderName.StartsWith(prefix, StringComparison.Ordinal))
                return ShaderRebindAction.BrokenStub;
        }

        return ShaderRebindAction.KeepModShipped;
    }

    // Sprite BEFORE Texture2D in every ordering below, without exception. A PNG
    // imported through Unity's TextureImporter is one bundle asset loadable as
    // either type (Texture2D main asset, Sprite sub-asset), so probing Texture2D
    // first would hide the sprite from the catalog and break KDL asset=
    // references typed against Sprite. Pure texture replacements have no sprite
    // sub-asset, miss the Sprite probe, and fall through cleanly.
    //
    // ProbeImage is the one ordering that demotes GameObject, so it is limited
    // to extensions whose importer emits no prefab. Formats that can carry one
    // (.psd through the PSD importer in rig mode) keep ProbePrefab.
    private static readonly AssetProbeKind[] ProbePrefab =
        { AssetProbeKind.GameObject, AssetProbeKind.Sprite, AssetProbeKind.Texture, AssetProbeKind.Audio };
    private static readonly AssetProbeKind[] ProbeImage =
        { AssetProbeKind.Sprite, AssetProbeKind.Texture, AssetProbeKind.GameObject, AssetProbeKind.Audio };
    private static readonly AssetProbeKind[] ProbeAudio =
        { AssetProbeKind.Audio, AssetProbeKind.GameObject, AssetProbeKind.Sprite, AssetProbeKind.Texture };

    /// <summary>
    /// Probe order for one bundle asset, hinted by its file extension. Every
    /// kind is tried in every ordering, so an unrecognised or wrong extension
    /// costs wasted probes and nothing else.
    /// </summary>
    internal static AssetProbeKind[] ProbeOrderFor(string assetName)
    {
        var dot = assetName.LastIndexOf('.');
        if (dot < 0 || dot == assetName.Length - 1)
            return ProbePrefab;

        var ext = assetName[(dot + 1)..].ToLowerInvariant();
        switch (ext)
        {
            case "wav":
            case "ogg":
            case "mp3":
            case "aif":
            case "aiff":
            case "flac":
            case "m4a":
                return ProbeAudio;
            case "png":
            case "jpg":
            case "jpeg":
            case "tga":
            case "bmp":
            case "exr":
            case "gif":
            case "tif":
            case "tiff":
            case "webp":
                return ProbeImage;
            default:
                // .prefab / .fbx / .gltf / .glb / .obj / .psd and anything
                // unknown keep the GameObject-first order.
                return ProbePrefab;
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

            log.Debug($"  Registered {mappedTargetKeys.Count} mesh target(s) from prefab '{prefab.name}'.");
        }
        finally
        {
            // The bundle instance is only a vehicle for extracting the (now-pinned) meshes;
            // nothing references it after registration, so it is always disposed.
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
        // back to the stub's dummy pass at render time. The runtime has the
        // real shaders loaded for the game's own assets, so Shader.Find(name)
        // resolves to a working shader of the same name. Material properties
        // (colors, textures, keywords) are preserved because we only swap the
        // shader pointer.
        //
        // A shader the mod authored itself travels inside this bundle as a
        // dependency of the material that references it, already compiled, so
        // its material is left exactly as it loaded. Such a shader is not
        // addressable by name here: GetAllAssetNames lists a bundle's
        // explicitly-assigned assets and not the dependencies pulled in
        // alongside them, so the shader never reaches the probe loop and the
        // classification runs off the name instead.
        var rebinds = 0;
        var modShipped = 0;
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

                var runtimeShader = name == "Hidden/InternalErrorShader" ? null : Shader.Find(name);
                switch (ClassifyShader(name, runtimeShader != null))
                {
                    case ShaderRebindAction.Rebind:
                        mat.shader = runtimeShader;
                        rebinds++;
                        break;
                    case ShaderRebindAction.KeepModShipped:
                        log.Debug($"    Material '{mat.name}' keeps mod-shipped shader '{name}'.");
                        modShipped++;
                        break;
                    default:
                        log.Warning(
                            name == "Hidden/InternalErrorShader"
                                ? $"    Material '{mat.name}' has a broken shader reference (dangling stub GUID at bake time) and will render magenta in-game. Re-import the reference prefab and re-bake."
                                : $"    Material '{mat.name}' references shader '{name}', which no runtime shader provides. An extraction stub of a shader this game does not ship renders wrong. Use a Menace/* shader, or ship your own shader under your own namespace.");
                        unresolved++;
                        break;
                }
            }
            renderer.sharedMaterials = mats;
        }

        var mirrorNotes = PrefabMirrors.Queue(prefab, key, log.Raw);

        if (_additionPrefabOwners.TryGetValue(key, out var previousOwner))
            log.Warning($"  Override addition prefab '{key}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        AdditionPrefabs[key] = prefab;
        _additionPrefabOwners[key] = ownerLabel;
        var shaderSuffix = $"; rebound {rebinds} shader(s)";
        if (modShipped > 0)
            shaderSuffix += $"; kept {modShipped} mod-shipped shader(s)";
        if (unresolved > 0)
            shaderSuffix += $"; {unresolved} unresolved (will render wrong)";
        log.Debug($"  Registered addition prefab: {key} (object name: {prefab.name}{shaderSuffix}{mirrorNotes})");
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
            log.Debug($"  Loaded compiled metadata for {result.Count} mesh asset(s)");

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

        if (result.Count > 0)
            log.Debug($"  Loaded {result.Count} mesh mapping(s) from jiangyu.json");
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
