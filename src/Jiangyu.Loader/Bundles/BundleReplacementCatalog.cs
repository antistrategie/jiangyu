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
    private MelonLogger.Instance _hostLog;

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

    // Textures, sprites, audio clips and addition prefabs, indexed by name at start and
    // loaded from their bundle on first use. Consumers ask by name and by type.
    public readonly LazyBundleAssets Assets;

    public BundleReplacementCatalog(List<UnityEngine.Object> pinned)
    {
        _pinned = pinned;
        Assets = new LazyBundleAssets(pinned) { OnPrefabLoaded = OnAdditionPrefabLoaded };
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
            ? new ModAssetRegistry(
                modId, bundles, _pinned, hostLog,
                key => Assets.TryGetAdditionPrefab(key, modId, out var prefab) ? prefab : null,
                (bundle, path) => Assets.TryGetAdditionPrefab(bundle, path, modId, out var prefab) ? prefab : null)
            : (IModAssets)NullModAssets.Instance;
        _assetsByMod[modId] = assets;
        return assets;
    }

    public BundleLoadSummary LoadBundles(ModLoadPlan plan, LoaderLog log)
    {
        _hostLog = log.Raw;
        Assets.Log = log.Raw;
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
        //
        // Mounting an LZ4 bundle reads its header and asset table and nothing
        // else, so everything below indexes names; an asset leaves the bundle
        // the first time something asks for it.
        var bundle = Il2CppAssetBundleManager.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            log.Error($"  LoadFromFile returned null for {Path.GetFileName(bundlePath)}");
            return;
        }

        if (!_bundlesByMod.TryGetValue(mod.Name, out var modBundles))
            _bundlesByMod[mod.Name] = modBundles = new List<Il2CppAssetBundle>();
        modBundles.Add(bundle);

        var assetNames = bundle.GetAllAssetNames();
        if (assetNames == null || assetNames.Length == 0)
        {
            log.Warning("  Bundle contains no assets.");
            return;
        }

        var goTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<GameObject>());

        // An addition bundle is one prefab filed under the bundle's own stem. The
        // prefab, its shader rebind and its script mirrors all wait for the first
        // request, so a character nobody fields in a session never leaves the bundle.
        if (isAdditionBundle)
        {
            // A UI document bundle sits on the same list and holds no prefab; it reaches
            // its mod through ModContext.Assets and must not claim a prefab key.
            var prefabName = PrefabEntryName(assetNames) ?? ProbeUnclassifiedPrefab(bundle, assetNames, goTypePtr);
            if (prefabName != null)
            {
                Assets.RegisterAdditionPrefab(bundleStem, bundle, prefabName, ownerLabel, mod.Name, log);
                log.Debug($"  Indexed addition prefab '{bundleStem}'.");
            }
            return;
        }

        Dictionary<string, string> bundleToGame = null;
        if (meshMappings != null)
        {
            bundleToGame = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (gameName, bundleName) in meshMappings)
                bundleToGame[bundleName] = gameName;
        }

        var meshTypePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.Of<Mesh>());
        var indexed = 0;

        foreach (var assetName in assetNames)
        {
            var stem = AssetStem(assetName);
            switch (ClassifyAssetName(assetName))
            {
                case BundleAssetClass.Audio:
                    Assets.RegisterAudioClip(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    indexed++;
                    break;

                case BundleAssetClass.Image:
                    // One imported image loads as a Sprite and as its Texture2D, so it
                    // answers to both; the request's type picks.
                    Assets.RegisterSprite(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    Assets.RegisterTexture(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    indexed++;
                    break;

                case BundleAssetClass.Serialised:
                    // A mod that replaces meshes ships them as generated assets whose
                    // compiled metadata the mesh applier reads off the object, so those
                    // register at start. Every other generated asset is a texture or a
                    // sprite object and waits for its first request.
                    if (meshMetadata != null)
                    {
                        var meshPtr = bundle.LoadAsset(assetName, meshTypePtr);
                        if (meshPtr != IntPtr.Zero)
                        {
                            RegisterMeshAsset(ownerLabel, meshPtr, bundleToGame, meshMetadata, log);
                            break;
                        }
                    }
                    Assets.RegisterTexture(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    Assets.RegisterSprite(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    indexed++;
                    break;

                case BundleAssetClass.Prefab:
                    // A prefab outside an addition bundle drives mesh replacement: its
                    // skinned meshes register under the manifest's mappings, which needs
                    // the object, so it loads at start as it always has. A PSD is in this
                    // class because rig mode imports one as a prefab; a PSD that turns out
                    // to be a plain image is indexed as one.
                    var prefabPtr = bundle.LoadAsset(assetName, goTypePtr);
                    if (prefabPtr != IntPtr.Zero)
                    {
                        RegisterPrefabAsset(ownerLabel, assetName, new GameObject(prefabPtr), bundleToGame, meshMetadata, log);
                        break;
                    }
                    Assets.RegisterSprite(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    Assets.RegisterTexture(stem, bundle, assetName, ownerLabel, mod.Name, log);
                    indexed++;
                    break;

                default:
                    // UI documents and other assets reach a mod through ModContext.Assets.
                    break;
            }
        }

        if (indexed > 0)
            log.Debug($"  Indexed {indexed} asset(s) for first-use loading.");
    }

    internal enum BundleAssetClass { Audio, Image, Serialised, Prefab, Other }

    /// <summary>
    /// What a bundle asset is, from its file extension alone, so the catalog can index it
    /// without loading it. Unity keeps the source file's extension in the asset path. A
    /// PSD counts as prefab-capable: imported in rig mode its main asset is a GameObject.
    /// </summary>
    internal static BundleAssetClass ClassifyAssetName(string assetName)
    {
        var dot = assetName.LastIndexOf('.');
        if (dot < 0 || dot == assetName.Length - 1)
            return BundleAssetClass.Other;

        switch (assetName[(dot + 1)..].ToLowerInvariant())
        {
            case "wav":
            case "ogg":
            case "mp3":
            case "aif":
            case "aiff":
            case "flac":
            case "m4a":
                return BundleAssetClass.Audio;
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
                return BundleAssetClass.Image;
            case "asset":
                return BundleAssetClass.Serialised;
            case "prefab":
            case "fbx":
            case "gltf":
            case "glb":
            case "obj":
            case "psd":
                return BundleAssetClass.Prefab;
            default:
                return BundleAssetClass.Other;
        }
    }

    /// <summary>The file stem of a bundle asset path, which is the name Unity gives the object inside.</summary>
    internal static string AssetStem(string assetName)
    {
        var slash = assetName.LastIndexOf('/');
        var leaf = slash >= 0 ? assetName[(slash + 1)..] : assetName;
        var dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }

    // A bundle built outside the compile pipeline may list its prefab under an
    // extensionless or unusual path that no extension names. Those entries are probed as
    // GameObject the one time, at start; a bundle without a prefab answers null to every
    // probe and claims no key. The compile pipeline's own bundles never reach this.
    private static string ProbeUnclassifiedPrefab(Il2CppAssetBundle bundle, IEnumerable<string> assetNames, IntPtr goTypePtr)
    {
        foreach (var assetName in assetNames)
        {
            if (ClassifyAssetName(assetName) != BundleAssetClass.Other)
                continue;
            if (bundle.LoadAsset(assetName, goTypePtr) != IntPtr.Zero)
                return assetName;
        }
        return null;
    }

    // The asset an addition bundle is loaded by: its first prefab by extension, or null
    // when none names one.
    internal static string PrefabEntryName(IEnumerable<string> assetNames)
    {
        foreach (var name in assetNames)
        {
            if (ClassifyAssetName(name) == BundleAssetClass.Prefab)
                return name;
        }
        return null;
    }


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

    // Runs once per addition prefab, from the lazy table, the first time a KDL asset=
    // reference or a ModContext.Assets load asks for it. The prefab is already pinned by
    // then; this pass makes it renderable and complete.
    private void OnAdditionPrefabLoaded(string key, string ownerLabel, string modId, GameObject prefab)
    {
        var log = new LoaderLog(_hostLog) { Mod = modId };

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

        var shaderSuffix = $"; rebound {rebinds} shader(s)";
        if (modShipped > 0)
            shaderSuffix += $"; kept {modShipped} mod-shipped shader(s)";
        if (unresolved > 0)
            shaderSuffix += $"; {unresolved} unresolved (will render wrong)";
        log.Debug($"  Loaded addition prefab on first use: {key} (object name: {prefab.name}{shaderSuffix}{mirrorNotes})");
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
