using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Replacements;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Discovers modder-authored files under <c>assets/replacements/&lt;cat&gt;/</c>
/// and <c>assets/additions/&lt;cat&gt;/</c> and produces the typed entries the
/// compile pipeline feeds into the bundle builder.
///
/// <para>Each replacement discoverer follows the same envelope: bail when
/// the category subdir is missing, load the asset index, collect candidate
/// files by extension, resolve each filename to its indexed target via the
/// category-specific Resolve helper, detect duplicate targets, and return
/// the result sorted by target name. Sprite discovery additionally splits
/// the matches between unique-backed sprites (which ride the standard
/// pipeline) and atlas-backed sprites grouped by backing texture (which
/// get composited at compile time).</para>
///
/// <para>Addition discoverers don't consult the asset index because the
/// asset name is the modder's choice rather than a target lookup; they walk
/// the directory and emit one entry per file, normalising slash-form
/// authoring names to the bundle's flat <c>__</c>-form via
/// <see cref="AssetCategory.ToBundleAssetName"/>.</para>
/// </summary>
internal static class ReplacementDiscovery
{
    private const string AssetIndexFileName = "asset-index.json";

    /// <summary>Result of sprite discovery: unique-backed sprites go through the normal pipeline,
    /// atlas-backed sprites are grouped for compile-time compositing.</summary>
    internal sealed class SpriteDiscoveryResult
    {
        public required List<GlbMeshBundleCompiler.ImportedSpriteAsset> UniqueSprites { get; init; }
        public required List<AtlasCompositor.AtlasGroup> AtlasGroups { get; init; }
    }

    public static List<GlbMeshBundleCompiler.CompiledTexture> DiscoverReplacementTextureEntries(string replacementsRoot, string cachePath, ILogSink log)
    {
        var texturesRoot = Path.Combine(replacementsRoot, "textures");
        if (!Directory.Exists(texturesRoot))
            return [];

        var index = CompilationService.LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var files = CompilationService.CollectAssetFiles(texturesRoot, "*.png", "*.jpg", "*.jpeg");
        if (files.Length == 0)
            return [];

        var entries = new List<GlbMeshBundleCompiler.CompiledTexture>();
        foreach (var file in files)
        {
            var targetName = Path.GetFileNameWithoutExtension(file);
            var target = CompilationService.ResolveReplacementTextureTarget(index, targetName, targetName, targetPathId: null, log);

            entries.Add(new GlbMeshBundleCompiler.CompiledTexture
            {
                Name = target.Name,
                Content = File.ReadAllBytes(file),
                Linear = GlbTextureExtractor.IsLinearTextureName(target.Name),
            });
        }

        var duplicateTargets = entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateTargets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple replacement textures resolve to the same target name under assets/replacements/textures: {string.Join(", ", duplicateTargets)}");
        }

        return [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    public static SpriteDiscoveryResult DiscoverReplacementSpriteEntries(string replacementsRoot, string cachePath, ILogSink log)
    {
        var emptyResult = new SpriteDiscoveryResult
        {
            UniqueSprites = [],
            AtlasGroups = [],
        };

        var spritesRoot = Path.Combine(replacementsRoot, "sprites");
        if (!Directory.Exists(spritesRoot))
            return emptyResult;

        var index = CompilationService.LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var files = CompilationService.CollectAssetFiles(spritesRoot, "*.png", "*.jpg", "*.jpeg");
        if (files.Length == 0)
            return emptyResult;

        var targetNames = new List<(string Name, string File)>();
        var uniqueEntries = new List<GlbMeshBundleCompiler.ImportedSpriteAsset>();
        var atlasReplacements = new Dictionary<(long PathId, string Collection), List<(ReplacementSpriteTarget Target, CompilationService.SpriteBackingClassification Classification, string File)>>();

        foreach (var file in files)
        {
            var targetName = Path.GetFileNameWithoutExtension(file);
            var (target, classification) = CompilationService.ResolveAndClassifySpriteTarget(index, targetName, targetName, targetPathId: null, log);
            targetNames.Add((target.Name, file));

            if (classification.IsAtlasBacked)
            {
                var key = (classification.BackingTexturePathId, classification.BackingTextureCollection ?? string.Empty);
                if (!atlasReplacements.TryGetValue(key, out var list))
                {
                    list = [];
                    atlasReplacements[key] = list;
                }
                list.Add((target, classification, file));
            }
            else
            {
                uniqueEntries.Add(new GlbMeshBundleCompiler.ImportedSpriteAsset
                {
                    Name = target.Name,
                    SourceFilePath = file,
                    Extension = Path.GetExtension(file),
                    StagingName = $"sprite_source__{target.Name}",
                });
            }
        }

        var duplicateTargets = targetNames
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateTargets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple replacement sprite files resolve to the same target name under assets/replacements/sprites: {string.Join(", ", duplicateTargets)}");
        }

        var atlasGroups = atlasReplacements
            .Select(kvp =>
            {
                var first = kvp.Value[0].Classification;
                return new AtlasCompositor.AtlasGroup
                {
                    AtlasName = first.BackingTextureName ?? $"pathId={first.BackingTexturePathId}",
                    AtlasPathId = first.BackingTexturePathId,
                    AtlasCollection = first.BackingTextureCollection ?? string.Empty,
                    Replacements = kvp.Value
                        .OrderBy(r => r.Target.Name, StringComparer.Ordinal)
                        .Select(r => new AtlasCompositor.AtlasSpriteReplacement
                        {
                            SpriteName = r.Target.Name,
                            SourceFilePath = r.File,
                            TextureRectX = r.Classification.TextureRectX,
                            TextureRectY = r.Classification.TextureRectY,
                            TextureRectWidth = r.Classification.TextureRectWidth,
                            TextureRectHeight = r.Classification.TextureRectHeight,
                            PackingRotation = r.Classification.PackingRotation,
                        })
                        .ToList(),
                };
            })
            .OrderBy(g => g.AtlasName, StringComparer.Ordinal)
            .ToList();

        return new SpriteDiscoveryResult
        {
            UniqueSprites = [.. uniqueEntries.OrderBy(entry => entry.Name, StringComparer.Ordinal)],
            AtlasGroups = atlasGroups,
        };
    }

    public static List<GlbMeshBundleCompiler.ImportedAudioAsset> DiscoverReplacementAudioEntries(string replacementsRoot, string cachePath, ILogSink log)
    {
        var audioRoot = Path.Combine(replacementsRoot, "audio");
        if (!Directory.Exists(audioRoot))
            return [];

        var index = CompilationService.LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var files = CompilationService.CollectAssetFiles(audioRoot, "*.wav", "*.ogg", "*.mp3");
        if (files.Length == 0)
            return [];

        var entries = new List<GlbMeshBundleCompiler.ImportedAudioAsset>();
        foreach (var file in files)
        {
            var targetName = Path.GetFileNameWithoutExtension(file);
            var target = CompilationService.ResolveAndValidateAudioTarget(index, targetName, targetName, targetPathId: null, file, log);

            entries.Add(new GlbMeshBundleCompiler.ImportedAudioAsset
            {
                Name = target.Name,
                SourceFilePath = file,
                Extension = Path.GetExtension(file),
            });
        }

        var duplicateTargets = entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateTargets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple replacement audio files resolve to the same target name under assets/replacements/audio: {string.Join(", ", duplicateTargets)}");
        }

        return [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    public static IEnumerable<GlbMeshBundleCompiler.CompiledTexture> DiscoverAdditionTextureEntries(string additionRoot)
    {
        var dir = Path.Combine(additionRoot, AssetCategory.Textures);
        if (!Directory.Exists(dir))
            return [];

        return CollectAdditionFiles(dir, AssetCategory.Textures)
            .Select(file => new GlbMeshBundleCompiler.CompiledTexture
            {
                Name = AssetCategory.ToBundleAssetName(AssetCategory.LogicalAdditionName(dir, file)),
                Content = File.ReadAllBytes(file),
                Linear = false,
            })
            .OrderBy(entry => entry.Name, StringComparer.Ordinal);
    }

    public static IEnumerable<GlbMeshBundleCompiler.ImportedSpriteAsset> DiscoverAdditionSpriteEntries(string additionRoot)
    {
        var dir = Path.Combine(additionRoot, AssetCategory.Sprites);
        if (!Directory.Exists(dir))
            return [];

        return CollectAdditionFiles(dir, AssetCategory.Sprites)
            .Select(file =>
            {
                var bundleName = AssetCategory.ToBundleAssetName(AssetCategory.LogicalAdditionName(dir, file));
                return new GlbMeshBundleCompiler.ImportedSpriteAsset
                {
                    Name = bundleName,
                    SourceFilePath = file,
                    Extension = Path.GetExtension(file),
                    StagingName = $"sprite_source__{bundleName}",
                    IsAddition = true,
                };
            })
            .OrderBy(entry => entry.Name, StringComparer.Ordinal);
    }

    public static IEnumerable<GlbMeshBundleCompiler.ImportedAudioAsset> DiscoverAdditionAudioEntries(string additionRoot)
    {
        var dir = Path.Combine(additionRoot, AssetCategory.Audio);
        if (!Directory.Exists(dir))
            return [];

        return CollectAdditionFiles(dir, AssetCategory.Audio)
            .Select(file => new GlbMeshBundleCompiler.ImportedAudioAsset
            {
                Name = AssetCategory.ToBundleAssetName(AssetCategory.LogicalAdditionName(dir, file)),
                SourceFilePath = file,
                Extension = Path.GetExtension(file),
            })
            .OrderBy(entry => entry.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Walk an additions category directory and collect every file whose
    /// extension is in the category's shared allowlist. Routing through
    /// <see cref="AssetCategory.AdditionExtensionsForCategory"/> keeps the
    /// compile pipeline and the studio picker in lockstep so the dropdown
    /// can never advertise a file the build wouldn't include.
    /// </summary>
    private static string[] CollectAdditionFiles(string directory, string category)
    {
        var extensions = AssetCategory.AdditionExtensionsForCategory(category);
        if (extensions.Count == 0)
            return [];
        var globs = new string[extensions.Count];
        for (var i = 0; i < extensions.Count; i++)
            globs[i] = "*" + extensions[i];
        return CompilationService.CollectAssetFiles(directory, globs);
    }
}
