using System.Security.Cryptography;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using Jiangyu.Shared;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using AssetRipper.Processing.Textures;
using AssetRipper.Export.Modules.Audio;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Assets;

public sealed class AssetPipelineService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    private const string IndexFileName = "asset-index.json";
    private const string ManifestFileName = "asset-index-manifest.json";
    private const string PreviewDirName = "previews";
    private const int ThumbnailMaxDimension = 256;
    // Bump on any change to AssetEntry shape or BuildAssetIndex behaviour
    // that older index files can't represent. Stale indexes get rebuilt by
    // GetIndexStatus's version check.
    //   v3: baseline (sprite atlas + AudioClip frequency/channels).
    //   v4: per-asset NamedChildren for prototype-source surfaces
    //       (Stem.SoundBank sounds[].name lookup).
    //   v5: per-asset BankId on Stem.SoundBank entries (replaces a
    //       hardcoded name→id table) and broadened SoundBank-detection
    //       filter that covers per-character bark banks (tactical_barks_*,
    //       strategic_barks_*), not just *_soundbank.
    //   v6: per-asset Roles list on ConversationTemplate entries (name +
    //       Guid pairs). Source of truth for compile-time RoleGuid string
    //       resolution and Studio's role combobox.
    //   v7: top-level TaggedDiscriminators dict keyed by polymorphic-base
    //       FQN. Sampled from vanilla m_SerializedNodes /
    //       m_SerializedRequirements / m_SerAction during the
    //       ConversationTemplate walk. Drives the discriminator-picker
    //       dropdown in the visual editor and tightens compile-time
    //       validation to reject discriminator forms vanilla doesn't
    //       accept.
    //   v8: per-asset Path on ConversationTemplate entries. Asset names
    //       are non-unique for conversations; Path is the unique
    //       identifier. Drives Studio's from= clone-source dropdown.
    //   v9: per-class metadata grouped under nested AssetSpriteMetadata /
    //       AssetAudioMetadata / AssetSoundBankMetadata /
    //       AssetConversationMetadata sub-objects on AssetEntry. Replaces
    //       the previous flat scatter of nullable Sprite*/Audio*/BankId/
    //       NamedChildren/Roles/Path fields.
    internal const int CurrentFormatVersion = 9;


    public string GameDataPath { get; } = gameDataPath;
    public string CachePath { get; } = cachePath;

    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    /// <summary>
    /// Checks whether the index is current by comparing the GameAssembly hash.
    /// </summary>
    public bool IsIndexCurrent()
    {
        return GetIndexStatus().IsCurrent;
    }

    public CachedIndexStatus GetIndexStatus()
    {
        return IndexCacheValidator.Validate(
            cacheFilePath: Path.Combine(CachePath, IndexFileName),
            manifestFilePath: Path.Combine(CachePath, ManifestFileName),
            loadManifest: path => JsonSerializer.Deserialize<IndexManifest>(
                File.ReadAllText(path), JsonOptions.PrettyPascalIgnoreNull),
            staleReason: manifest =>
            {
                var currentHash = ComputeGameAssemblyHash();
                if (currentHash is null
                    || currentHash != manifest.GameAssemblyHash
                    || manifest.FormatVersion != CurrentFormatVersion)
                {
                    return "Asset index is stale for the current game version. Run 'jiangyu assets index' first.";
                }
                return null;
            },
            missingReason: "Asset index not found. Run 'jiangyu assets index' first.",
            unreadableManifestReason: "Asset index manifest is unreadable. Rebuild it with 'jiangyu assets index'.");
    }

    /// <summary>
    /// Loads game data via AssetRipper, builds a searchable asset index, and writes it to the cache.
    /// No asset files are exported — only metadata. Always rebuilds from scratch.
    /// </summary>
    public void BuildIndex()
    {
        using var session = LoadAndProcessGameData();
        BuildIndexFromGameData(session.GameData);
    }

    /// <summary>
    /// Builds the asset index from pre-loaded game data. Use this overload when
    /// the caller already holds a <see cref="GameData"/> instance (e.g. from a
    /// cached load) to avoid re-loading.
    /// </summary>
    public void BuildIndexFromGameData(GameData gameData)
    {
        _progress.SetPhase("Building index");
        var index = AssetIndexBuilder.Build(gameData);
        _progress.Finish();

        // Wipe cached previews so stale entries from an older game version
        // (which may reuse the same collection/pathId) don't survive.
        var previewDir = Path.Combine(CachePath, PreviewDirName);
        if (Directory.Exists(previewDir))
            Directory.Delete(previewDir, recursive: true);

        // Write index
        Directory.CreateDirectory(CachePath);
        File.WriteAllText(
            Path.Combine(CachePath, IndexFileName),
            JsonSerializer.Serialize(index, JsonOptions.PrettyPascalIgnoreNull));

        // Write manifest
        var manifest = new IndexManifest
        {
            FormatVersion = CurrentFormatVersion,
            GameAssemblyHash = ComputeGameAssemblyHash(),
            IndexedAt = DateTimeOffset.UtcNow,
            GameDataPath = GameDataPath,
            AssetCount = index.Assets?.Count ?? 0,
        };
        File.WriteAllText(
            Path.Combine(CachePath, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions.PrettyPascalIgnoreNull));

        _log.Info($"Indexed {index.Assets?.Count ?? 0} assets to: {CachePath}");
    }

    /// <summary>
    /// Loads game data via AssetRipper and runs all processors. Returns the
    /// fully-processed <see cref="GameData"/> ready for asset extraction.
    /// Callers may cache the result for repeated lookups.
    /// </summary>
    public GameDataSession LoadAndProcessGameData()
    {
        _log.Info($"Loading game data from: {GameDataPath}");

        // Level2 inflates MonoBehaviour structures via the typed managed
        // metadata. Required for the asset-index build to walk
        // Stem.SoundBank.sounds[] and populate AssetEntry.NamedChildren;
        // lower levels leave m_Structure as an opaque node with no Fields list.
        // includeSpriteProcessor: the asset-index pipeline is the only caller
        // that needs SpriteInformationObject results.
        var session = new GameDataSession(GameDataPath, _progress, includeSpriteProcessor: true);

        if (!session.HasAnyAssetCollections)
        {
            session.Dispose();
            throw new InvalidOperationException("No asset collections found in game data.");
        }

        int collectionCount = session.GameData.GameBundle.FetchAssetCollections().Count();
        _log.Info($"Loaded {collectionCount} asset collections");

        return session;
    }

    /// <summary>
    /// Loads and returns the asset index from the cache. Returns null if no index exists.
    /// </summary>
    public AssetIndex? LoadIndex()
    {
        var indexPath = Path.Combine(CachePath, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AssetIndex>(File.ReadAllText(indexPath), JsonOptions.PrettyPascalIgnoreNull);
    }

    public IndexManifest? LoadManifest()
    {
        var manifestPath = Path.Combine(CachePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<IndexManifest>(File.ReadAllText(manifestPath), JsonOptions.PrettyPascalIgnoreNull);
    }

    /// <summary>
    /// Searches the asset index with optional query and type filter.
    /// Returns matching entries, up to the specified limit.
    /// Returns an empty list if no index exists.
    /// </summary>
    public List<AssetEntry> Search(string? query = null, string? typeFilter = null, int limit = 50)
    {
        var index = LoadIndex();
        if (index?.Assets is null)
        {
            return [];
        }

        return [.. index.Assets
            .Where(a =>
                (string.IsNullOrEmpty(query)
                 || (a.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                 || (a.CanonicalPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                && (typeFilter is null || string.Equals(a.ClassName, typeFilter, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)];
    }

    /// <summary>
    /// Resolves the first asset matching the given name and any of the specified class names.
    /// Returns null if no index exists or no match is found.
    /// </summary>
    public AssetEntry? ResolveAsset(string assetName, params string[] classNames)
    {
        var index = LoadIndex();
        if (index?.Assets is null)
        {
            return null;
        }

        return index.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase)
            && classNames.Any(cn => string.Equals(a.ClassName, cn, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Returns every asset matching the given name and any of the specified class names,
    /// optionally narrowed to a specific collection and/or pathId. Caller decides how to
    /// handle zero / one / many matches.
    /// </summary>
    public IReadOnlyList<AssetEntry> FindAssets(
        string assetName,
        IReadOnlyList<string> classNames,
        string? collection = null,
        long? pathId = null)
    {
        var index = LoadIndex();
        if (index?.Assets is null)
        {
            return [];
        }

        return [.. index.Assets.Where(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase)
            && classNames.Any(cn => string.Equals(a.ClassName, cn, StringComparison.OrdinalIgnoreCase))
            && (collection is null || string.Equals(a.Collection, collection, StringComparison.OrdinalIgnoreCase))
            && (!pathId.HasValue || a.PathId == pathId.Value))];
    }

    // JIANGYU-CONTRACT: PrefabHierarchyObject is the preferred modder-facing model target.
    // Both compile-time target resolution and CLI model export collapse a PHO to the single
    // same-named GameObject via the asset index, then work against that GameObject. Valid for
    // the current proven export/replacement path; ambiguity is a hard error rather than a guess.
    /// <summary>
    /// If <paramref name="target"/> is a PrefabHierarchyObject, returns the single
    /// same-named GameObject from <paramref name="index"/> that backs it. Otherwise returns
    /// <paramref name="target"/> unchanged. Throws when the backing GameObject is missing
    /// or ambiguous.
    /// </summary>
    public static AssetEntry ResolveGameObjectBacking(AssetIndex index, AssetEntry target)
    {
        if (!string.Equals(target.ClassName, "PrefabHierarchyObject", StringComparison.Ordinal))
        {
            return target;
        }

        var name = target.Name ?? string.Empty;
        var backing = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "GameObject", StringComparison.Ordinal) &&
                string.Equals(entry.Name, name, StringComparison.Ordinal))
            .ToList()
            ?? [];

        if (backing.Count == 0)
        {
            throw new InvalidOperationException(
                $"PrefabHierarchyObject '{name}' has no backing GameObject of the same name in the asset index.");
        }

        if (backing.Count > 1)
        {
            var matches = backing
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"PrefabHierarchyObject '{name}' has multiple backing GameObjects: {string.Join(", ", matches)}");
        }

        return backing[0];
    }

    /// <summary>
    /// Extracts a vanilla game prefab plus its referenced assets (meshes,
    /// materials, textures) as Unity-native files into
    /// <paramref name="destDir"/>. The output is shaped so a modder can drop
    /// it under <c>unity/Assets/Imported/&lt;name&gt;/</c> and Unity Editor
    /// imports it as a reference / clone-from-base for prefab cloning work.
    ///
    /// Implementation strategy: runs AssetRipper's full project export to a
    /// game-version-keyed cache directory, then surgically copies the target
    /// prefab and its transitively-referenced files out of the cache into
    /// <paramref name="destDir"/>. The first call for a given game version is
    /// slow (full extraction); subsequent calls reuse the cache.
    /// </summary>
    /// <param name="destDir">Output directory. Created if missing.</param>
    /// <param name="collection">Optional collection name from `assets search`.</param>
    /// <param name="pathId">Optional path ID from `assets search`. Pass -1 to resolve by name only.</param>
    public void ImportPrefabAsUnityAssets(string assetName, string destDir, string? collection, long pathId)
        => new PrefabImportService(GameDataPath, _progress, _log)
            .ImportPrefabAsUnityAssets(assetName, destDir, collection, pathId);

    /// <summary>
    /// Overload that reuses an already-loaded <see cref="GameData"/>. Callers
    /// that hold a session-cached instance (Studio's <c>_cachedGameData</c>)
    /// take this path to skip the multi-second <c>GameStructure.Load</c> +
    /// <c>RunProcessors</c> cold start.
    /// </summary>
    public void ImportPrefabAsUnityAssets(GameData gameData, string assetName, string destDir, string? collection, long pathId)
        => new PrefabImportService(GameDataPath, _progress, _log)
            .ImportPrefabAsUnityAssets(gameData, assetName, destDir, collection, pathId);

    /// <summary>
    /// Loads game data, finds the named asset, and exports a self-contained model package.
    /// Clean export layout:
    ///   {packageDir}/model.gltf          (references textures via standard glTF material channels)
    ///   {packageDir}/textures/*.png
    /// Raw export layout:
    ///   {packageDir}/model.glb            (self-contained, no textures)
    /// </summary>
    /// <param name="collection">The asset collection name (from index).</param>
    /// <param name="pathId">The asset PathID (from index).</param>
    public void ExportModel(string assetName, string packageDir, bool clean, string collection, long pathId)
        => new ModelExportService(GameDataPath, _progress, _log)
            .ExportModel(assetName, packageDir, clean, collection, pathId);

    private AssetExportService ExportService()
        => new AssetExportService(GameDataPath, LoadIndex(), _progress, _log);

    /// <summary>
    /// Decodes the indexed Texture2D asset to a PNG at <paramref name="outputFilePath"/>.
    /// </summary>
    public bool ExportTexture(string assetName, string outputFilePath, string collection, long pathId)
        => ExportService().ExportTexture(assetName, outputFilePath, collection, pathId);

    /// <summary>
    /// Loads the indexed Texture2D asset and decodes it to RGBA32 pixel data.
    /// </summary>
    public (int Width, int Height, byte[] Rgba)? LoadTexture2dRgba(string assetName, string collection, long pathId)
        => ExportService().LoadTexture2dRgba(assetName, collection, pathId);

    /// <summary>
    /// Decodes the indexed Sprite asset to a PNG at <paramref name="outputFilePath"/>.
    /// </summary>
    public bool ExportSprite(string assetName, string outputFilePath, string collection, long pathId)
        => ExportService().ExportSprite(assetName, outputFilePath, collection, pathId);

    /// <summary>
    /// Decodes the indexed AudioClip asset. Writes to <paramref name="outputDirectory"/>.
    /// </summary>
    public string? ExportAudio(string assetName, string outputDirectory, string collection, long pathId)
        => ExportService().ExportAudio(assetName, outputDirectory, collection, pathId);

    /// <summary>
    /// Exports the atlas Texture2D as a PNG with coloured sprite-region outlines plus a legend.
    /// </summary>
    public bool ExportAtlas(string atlasName, string outputFilePath, string collection, long pathId)
        => ExportService().ExportAtlas(atlasName, outputFilePath, collection, pathId);


    internal static string BuildCanonicalAssetPath(string? collectionName, string? className, string? assetName, long pathId)
    {
        var collectionSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(collectionName) ? "unknown-collection" : collectionName);
        var classSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(className) ? "UnknownClass" : className);
        var nameSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(assetName) ? "unnamed" : assetName);
        return $"{collectionSegment}/{classSegment}/{nameSegment}--{pathId}";
    }

    /// <summary>
    /// Replaces every character outside <c>[A-Za-z0-9._-]</c> with <c>_</c>. Used for
    /// building canonical asset paths and any filename derived from an asset name.
    /// Empty input collapses to <c>"_"</c> so callers always get a usable segment.
    /// </summary>
    public static string SanitizeAssetPathSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                buffer[index++] = c;
            }
            else
            {
                buffer[index++] = '_';
            }
        }

        return index == 0 ? "_" : new string(buffer[..index]);
    }

    internal static ITexture2D? ResolveSpriteBackingTexture(
        ISprite sprite)
    {
        // Non-atlas sprites carry the texture reference directly on sprite.RD.
        var direct = sprite.RD.Texture.TryGetAsset(sprite.Collection);
        if (direct is not null)
            return direct;

        // Atlas-packed sprites carry a SpriteAtlas pointer; the backing texture lives
        // in the atlas's RenderDataMap, keyed by the sprite's RenderDataKey. This
        // mirrors AssetRipper.Processing.Textures.SpriteProcessor.ProcessSprite, which
        // we intentionally don't run as a processor here because it mutates sprite.Rect
        // /Pivot/Border and clears SpriteAtlasP, side-effects we don't want to apply to
        // the indexed game data.
        var atlas = sprite.SpriteAtlasP;
        if (atlas is null || !sprite.Has_RenderDataKey())
            return null;

        if (!atlas.RenderDataMap.TryGetValue(sprite.RenderDataKey, out var atlasData))
            return null;

        return atlasData.Texture.TryGetAsset(atlas.Collection);
    }

    /// <summary>
    /// Extracts the sprite's atlas-space textureRect and packing rotation from either the
    /// SpriteAtlas RenderDataMap (for atlas-packed sprites) or the sprite's own RenderData
    /// (for standalone sprites). Populates the corresponding <see cref="AssetSpriteMetadata"/> fields.
    /// </summary>
    internal static void PopulateSpriteAtlasMetadata(ISprite sprite, AssetSpriteMetadata meta)
    {
        var atlas = sprite.SpriteAtlasP;
        if (atlas is not null && sprite.Has_RenderDataKey()
            && atlas.RenderDataMap.TryGetValue(sprite.RenderDataKey, out var atlasData))
        {
            var rect = atlasData.TextureRect;
            meta.TextureRectX = rect.X;
            meta.TextureRectY = rect.Y;
            meta.TextureRectWidth = rect.Width;
            meta.TextureRectHeight = rect.Height;
            meta.PackingRotation = (int)(atlasData.SettingsRaw >> 2 & 0xF);
        }
        else
        {
            var rd = sprite.RD;
            var rect = rd.TextureRect;
            meta.TextureRectX = rect.X;
            meta.TextureRectY = rect.Y;
            meta.TextureRectWidth = rect.Width;
            meta.TextureRectHeight = rect.Height;
            meta.PackingRotation = (int)(rd.SettingsRaw >> 2 & 0xF);
        }
    }

    private string? ComputeGameAssemblyHash() => GameDataSession.ComputeGameAssemblyHash(GameDataPath);

    /// <summary>
    /// Convenience facade so callers holding an <see cref="AssetPipelineService"/>
    /// don't need to construct an <see cref="AssetPreviewService"/> separately.
    /// </summary>
    public AssetPreviewResult? GeneratePreview(GameData gameData, string collection, long pathId, string className)
        => new AssetPreviewService(CachePath, _log).GeneratePreview(gameData, collection, pathId, className);
}
