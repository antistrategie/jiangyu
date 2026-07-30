using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Name validation across every source that feeds the Unity replacement-bundle build.
/// Each runtime catalogue (audio, sprite, texture) is keyed by bare asset name, staged
/// audio lands flat in one <c>Audio/</c> directory, and each generated asset bakes to
/// <c>Generated/&lt;name&gt;.asset</c>, so two sources resolving to the same name
/// clobber silently: whichever lands last wins the staged file, the baked asset, or
/// the catalogue slot. Surfaced here as compile errors before any Unity work runs.
/// The per-category checks in <see cref="Templates.FileSystemAssetAdditionsCatalog"/>
/// cover same-stem files inside one additions folder; this covers the merged sets
/// (replacements plus additions, and the flattened names they bake to).
/// </summary>
internal static class ReplacementNameValidation
{
    internal static List<string> FindConflicts(
        IReadOnlyList<GlbMeshBundleCompiler.ImportedAudioAsset> audio,
        IReadOnlyList<GlbMeshBundleCompiler.ImportedSpriteAsset> sprites,
        IReadOnlyList<GlbMeshBundleCompiler.CompiledTexture> textures,
        IEnumerable<string> meshBundleNames,
        string replacementBundleName,
        IEnumerable<string> additionPrefabStems)
    {
        var conflicts = new List<string>();

        // One flat staged directory and one runtime catalogue per kind: a duplicate
        // name within a kind is always a clobber, whichever sources it came from.
        AddDuplicates(conflicts, "audio", audio.Select(a => (a.Name, a.SourceFilePath)));
        AddDuplicates(conflicts, "sprite", sprites.Select(s => (s.Name, s.SourceFilePath)));

        conflicts.AddRange(FindGeneratedConflicts(
            textures.Select(t => t.Name), meshBundleNames, sprites));

        // Addition prefab bundles and the replacement bundles ship side by side in
        // compiled/bundles/. The replacement bundle files are named <mod> or
        // <mod>__<category>[__<group>] over a fixed category set, so only prefab
        // names landing on one of those shapes can overwrite them; a prefab merely
        // named <mod>__<something-else> (the Character/Character folder convention
        // flattens that way for a mod named after its character) is fine. Unity
        // lowercases bundle file names on write, so compare case-insensitively.
        foreach (var stem in additionPrefabStems)
        {
            var flattened = stem.Replace("/", "__");
            if (IsReplacementBundleShape(flattened, replacementBundleName))
                conflicts.Add(
                    $"addition prefab '{stem}' builds to bundle '{flattened}.bundle', which collides with one of the mod's replacement bundle names ('{replacementBundleName}' or '{replacementBundleName}__<category>[__<group>]'). Rename the prefab.");
        }

        return conflicts;
    }

    private static readonly string[] ReplacementCategories = ["audio", "sprites", "textures", "meshes"];

    private static bool IsReplacementBundleShape(string flattenedStem, string replacementBundleName)
    {
        if (string.Equals(flattenedStem, replacementBundleName, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var category in ReplacementCategories)
        {
            var prefix = $"{replacementBundleName}__{category}";
            if (string.Equals(flattenedStem, prefix, StringComparison.OrdinalIgnoreCase)
                || flattenedStem.StartsWith($"{prefix}__", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Duplicates in the <c>Generated/&lt;name&gt;.asset</c> path space: textures (a caller
    /// may include GLB-extracted names alongside the discovered ones), replacement meshes,
    /// and each replacement sprite's pair (its Sprite object plus its
    /// <c>sprite_source__</c> texture).
    /// </summary>
    internal static List<string> FindGeneratedConflicts(
        IEnumerable<string> textureNames,
        IEnumerable<string> meshBundleNames,
        IEnumerable<GlbMeshBundleCompiler.ImportedSpriteAsset> sprites)
    {
        var conflicts = new List<string>();
        var generated = textureNames.Select(t => (Name: t, Source: $"texture '{t}'"))
            .Concat(meshBundleNames.Select(m => (Name: m, Source: $"replacement mesh '{m}'")))
            .Concat(sprites.Where(s => !s.IsAddition).SelectMany(s => new[]
            {
                (s.Name, Source: $"replacement sprite '{s.SourceFilePath}'"),
                (Name: s.StagingName, Source: $"replacement sprite '{s.SourceFilePath}'"),
            }));
        AddDuplicates(conflicts, "generated asset", generated);
        return conflicts;
    }

    // Grouping is case-insensitive because every namespace this guards is: staged files
    // and Generated/ assets land on case-insensitive filesystems on Windows and macOS,
    // and Unity's asset database treats paths the same way.
    private static void AddDuplicates(List<string> conflicts, string kind, IEnumerable<(string Name, string Source)> entries)
    {
        foreach (var group in entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sources = string.Join(", ", group.Select(e => e.Source).Distinct(StringComparer.Ordinal));
            conflicts.Add(
                $"{kind} name '{group.Key}' is produced by more than one source ({sources}). The build would keep only one; rename or remove the duplicates.");
        }
    }
}
