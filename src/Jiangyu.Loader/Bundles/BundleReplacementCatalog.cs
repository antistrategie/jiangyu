using Il2CppInterop.Runtime;
using Jiangyu.Shared.Bundles;
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
    private readonly Dictionary<string, string> _spriteOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _audioOwners = new(StringComparer.Ordinal);
    // Mirrors AdditionPrefabs' case-insensitive keying so duplicate-detection
    // stays consistent when a later mod ships the same prefab under a
    // different-cased name.
    private readonly Dictionary<string, string> _additionPrefabOwners = new(StringComparer.OrdinalIgnoreCase);

    // Humanoid addition prefabs need their MonoBehaviour config
    // mirrored from a canonical reference vanilla soldier prefab —
    // AssetRipper strips Ragdoll / Footprints field values on extract,
    // so the addition reaches the loader with the components attached
    // by bake but no field data. (Hierarchy children like dust spawn
    // markers are mirrored at bake time inside BakeHumanoid, where
    // Editor APIs can mutate the prefab on disk.) The reference lookup
    // needs MENACE's asset registry to be populated, which isn't true
    // during early-boot mod load, so we queue prefabs here and drain
    // via MirrorPendingHumanoidPrefabs once the loader's
    // ApplyReplacements pass starts running.
    private readonly List<GameObject> _pendingHumanoidMirrors = new();

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

    public BundleLoadSummary LoadBundles(ModLoadPlan plan, MelonLogger.Instance log)
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

        // Parse the mod's jiangyu.json once and reuse the JsonElement across
        // the three readers. LoadBundle runs per bundle, but the manifest is
        // one file per mod, so the previous parse-three-times-per-bundle shape
        // was O(bundles × 3) reads of the same file at startup.
        Dictionary<string, string> meshMappings = null;
        Dictionary<string, CompiledMeshMetadataRecord> meshMetadata = null;
        HashSet<string> additionPrefabNames = new(StringComparer.Ordinal);
        using (var manifestDoc = OpenManifest(mod.ManifestPath, log))
        {
            if (manifestDoc != null)
            {
                var root = manifestDoc.RootElement;
                meshMappings = LoadMeshMappings(root, log);
                meshMetadata = LoadCompiledMeshMetadata(root, log);
                additionPrefabNames = LoadAdditionPrefabNames(root, log);
            }
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
        Dictionary<string, CompiledMeshMetadataRecord> meshMetadata,
        MelonLogger.Instance log)
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
                log.Msg($"  {assetName}: no SkinnedMeshRenderers found, skipping");
                return;
            }

            var mappedTargetKeys = new HashSet<string>(StringComparer.Ordinal);
            var metadataByTargetKey = new Dictionary<string, CompiledMeshMetadataRecord>(StringComparer.Ordinal);
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

                var materialBindings = Array.Empty<CompiledMaterialBindingRecord>();
                var targetRendererPath = targetKey;
                string targetEntityName = null;
                long targetEntityPathId = 0;
                var hasTargetEntityPathId = false;
                if (meshMetadata != null && meshMetadata.TryGetValue(bundleMeshName, out var prefabMetadata))
                {
                    materialBindings = prefabMetadata.MaterialBindings ?? Array.Empty<CompiledMaterialBindingRecord>();
                    targetRendererPath = string.IsNullOrWhiteSpace(prefabMetadata.TargetRendererPath)
                        ? targetKey
                        : prefabMetadata.TargetRendererPath;
                    targetEntityName = prefabMetadata.TargetEntityName;
                    targetEntityPathId = prefabMetadata.TargetEntityPathId;
                    hasTargetEntityPathId = prefabMetadata.HasTargetEntityPathId;
                    metadataByTargetKey[targetKey] = prefabMetadata;
                }

                RegisterMeshOverride(
                    targetKey,
                    new ReplacementMesh(mesh, GetBoneNames(smr.bones), materialBindings, targetRendererPath, targetEntityName, targetEntityPathId, hasTargetEntityPathId),
                    ownerLabel,
                    log);
                mappedTargetKeys.Add(targetKey);
                log.Msg($"  Registered: {bundleMeshName} -> {targetKey} ({smr.bones.Length} bones, materialBindings={materialBindings.Length})");
            }

            if (mappedTargetKeys.Count == 0)
                return;

            var prefabBoneNames = CollectPrefabBoneNames(instance);
            foreach (var targetKey in mappedTargetKeys)
            {
                var targetRendererPath = targetKey;
                string targetEntityName = null;
                long targetEntityPathId = 0;
                var hasTargetEntityPathId = false;
                if (metadataByTargetKey.TryGetValue(targetKey, out var prefabMetadata))
                {
                    targetRendererPath = string.IsNullOrWhiteSpace(prefabMetadata.TargetRendererPath)
                        ? targetKey
                        : prefabMetadata.TargetRendererPath;
                    targetEntityName = prefabMetadata.TargetEntityName;
                    targetEntityPathId = prefabMetadata.TargetEntityPathId;
                    hasTargetEntityPathId = prefabMetadata.HasTargetEntityPathId;
                }

                RegisterPrefabOverride(
                    targetKey,
                    new ReplacementPrefab(instance, prefab.name, prefabBoneNames, targetRendererPath, targetEntityName, targetEntityPathId, hasTargetEntityPathId),
                    ownerLabel,
                    log);
            }

            instance.transform.position = HiddenTemplateInstancePosition;
            instance.SetActive(false);
            _pinned.Add(instance);
            keepTemplateInstance = true;
            log.Msg($"  Registered prefab: {prefab.name} -> {mappedTargetKeys.Count} target renderer path(s) ({prefabBoneNames.Length} bones)");
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

    private void RegisterAudioAsset(string ownerLabel, IntPtr audioClipPtr, MelonLogger.Instance log)
    {
        var loadedClip = new AudioClip(audioClipPtr)
        {
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        RegisterAudioOverride(loadedClip.name, loadedClip, ownerLabel, log);
        _pinned.Add(loadedClip);
        log.Msg($"  Registered audio asset: {loadedClip.name}");
    }

    private void RegisterSpriteAsset(string ownerLabel, IntPtr spritePtr, MelonLogger.Instance log)
    {
        var loadedSprite = new Sprite(spritePtr)
        {
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        RegisterSpriteOverride(loadedSprite.name, loadedSprite, ownerLabel, log);
        _pinned.Add(loadedSprite);
        log.Msg($"  Registered sprite asset: {loadedSprite.name}");

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
    private void RegisterAdditionPrefab(string ownerLabel, GameObject prefab, string key, MelonLogger.Instance log)
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

        var soldierScripts = QueueHumanoidMirrorIfApplicable(prefab, log);

        if (_additionPrefabOwners.TryGetValue(key, out var previousOwner))
            log.Warning($"  Override addition prefab '{key}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        AdditionPrefabs[key] = prefab;
        _additionPrefabOwners[key] = ownerLabel;
        var shaderSuffix = unresolved > 0
            ? $"; rebound {rebinds} shader(s); {unresolved} unresolved (will render magenta)"
            : $"; rebound {rebinds} shader(s)";
        log.Msg($"  Registered addition prefab: {key} (object name: {prefab.name}{shaderSuffix}{soldierScripts})");
    }

    /// <summary>
    /// Try to mirror a humanoid addition prefab's script-config from
    /// the named reference soldier immediately at registration time;
    /// queue it for the next ApplyReplacements pass if the reference
    /// isn't loaded yet. Mirroring at registration time is preferred
    /// because MENACE can pre-instantiate the prefab between bundle
    /// load and the loader's first ApplyReplacements tick (e.g. for
    /// leader portrait warmup), and any pre-mirror clones would miss
    /// the dust markers and other supplementary children. Returns a
    /// short log suffix describing the outcome for the caller's
    /// register line.
    /// </summary>
    private string QueueHumanoidMirrorIfApplicable(GameObject prefab, MelonLogger.Instance log)
    {
        if (!HumanoidPrefabMirror.IsHumanoid(prefab))
            return string.Empty;
        if (HumanoidPrefabMirror.Mirror(prefab, log))
            return "; humanoid-mirrored";
        _pendingHumanoidMirrors.Add(prefab);
        return "; queued for humanoid mirror";
    }

    /// <summary>
    /// Drain the humanoid-mirror queue and patch any live instances
    /// that beat the mirror to the punch. Called from the loader's
    /// ApplyReplacements pass; by that point the game's
    /// ScriptableObjects and reference prefabs are reachable through
    /// Resources. The instance scan covers <c>main(Clone)</c>-style
    /// objects that MENACE pre-instantiated before the reference
    /// lookup could succeed (leader portrait warmup etc.) — they
    /// inherit the un-mirrored sentinel from their prefab, and the
    /// mirror picks them up by walking the GameObject registry.
    /// </summary>
    public void MirrorPendingHumanoidPrefabs(MelonLogger.Instance log)
    {
        if (_pendingHumanoidMirrors.Count == 0) return;

        var mirrored = 0;
        _pendingHumanoidMirrors.RemoveAll(prefab =>
        {
            if (prefab == null) return true;
            if (!HumanoidPrefabMirror.Mirror(prefab, log)) return false;
            mirrored++;
            return true;
        });

        if (mirrored > 0)
            log.Msg($"Humanoid mirror: {mirrored} addition prefab(s) configured from vanilla reference.");
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
                metadataForMesh.MaterialBindings ?? Array.Empty<CompiledMaterialBindingRecord>(),
                targetRendererPath,
                metadataForMesh.TargetEntityName,
                metadataForMesh.TargetEntityPathId,
                metadataForMesh.HasTargetEntityPathId),
            ownerLabel,
            log);
        log.Msg($"  Registered mesh asset: {loadedMesh.name} -> {targetKey} ({metadataForMesh.BoneNames.Length} bones, materialBindings={metadataForMesh.MaterialBindings?.Length ?? 0})");
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

    private void RegisterSpriteOverride(string spriteName, Sprite sprite, string ownerLabel, MelonLogger.Instance log)
    {
        if (_spriteOwners.TryGetValue(spriteName, out var previousOwner))
            log.Warning($"  Override sprite '{spriteName}': later-loaded mod '{ownerLabel}' replaces '{previousOwner}'.");

        ReplacementSprites[spriteName] = sprite;
        _spriteOwners[spriteName] = ownerLabel;
    }

    private void RegisterAudioOverride(string clipName, AudioClip clip, string ownerLabel, MelonLogger.Instance log)
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

    private static System.Text.Json.JsonDocument OpenManifest(string manifestPath, MelonLogger.Instance log)
    {
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            return System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read jiangyu.json: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, CompiledMeshMetadataRecord> LoadCompiledMeshMetadata(System.Text.Json.JsonElement root, MelonLogger.Instance log)
    {
        try
        {
            if (!root.TryGetProperty("meshes", out var meshesElement) ||
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

                string targetRendererPath = null;
                string targetMeshName = null;
                string targetEntityName = null;
                long targetEntityPathId = 0;
                var hasTargetEntityPathId = false;
                if (compiledElement.TryGetProperty("targetRendererPath", out var targetRendererPathElement) &&
                    targetRendererPathElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    targetRendererPath = targetRendererPathElement.GetString();
                }
                if (compiledElement.TryGetProperty("targetMeshName", out var targetMeshNameElement) &&
                    targetMeshNameElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    targetMeshName = targetMeshNameElement.GetString();
                }
                if (compiledElement.TryGetProperty("targetEntityName", out var targetEntityNameElement) &&
                    targetEntityNameElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    targetEntityName = targetEntityNameElement.GetString();
                }
                if (compiledElement.TryGetProperty("targetEntityPathId", out var targetEntityPathIdElement) &&
                    targetEntityPathIdElement.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    targetEntityPathIdElement.TryGetInt64(out var parsedTargetEntityPathId))
                {
                    targetEntityPathId = parsedTargetEntityPathId;
                    hasTargetEntityPathId = true;
                }

                result[bundleMeshName] = new CompiledMeshMetadataRecord
                {
                    BoneNames = boneNames,
                    MaterialBindings = LoadCompiledMaterialBindings(compiledElement),
                    TargetRendererPath = targetRendererPath,
                    TargetMeshName = targetMeshName,
                    TargetEntityName = targetEntityName,
                    TargetEntityPathId = targetEntityPathId,
                    HasTargetEntityPathId = hasTargetEntityPathId,
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

    private static HashSet<string> LoadAdditionPrefabNames(System.Text.Json.JsonElement root, MelonLogger.Instance log)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (root.TryGetProperty("additionPrefabs", out var element) &&
                element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            set.Add(name!);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"  Failed to read additionPrefabs from jiangyu.json: {ex.Message}");
        }
        return set;
    }

    private static Dictionary<string, string> LoadMeshMappings(System.Text.Json.JsonElement root, MelonLogger.Instance log)
    {
        try
        {
            if (!root.TryGetProperty("meshes", out var meshesElement))
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
