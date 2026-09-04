using Jiangyu.Core.Compile;

namespace Jiangyu.Core.Glb;

/// <summary>
/// Compile-side authority for how the replacement output splits into AssetBundles.
/// Audio and textures group per character folder (the segment before the first
/// <c>__</c> the additions tree flattens in), sprites and meshes each ship as one
/// bundle, so an edit rebuilds only the group it touches. The plan is written as a
/// tab-separated file the Unity build consumes verbatim, keeping the grouping rule
/// in one place. Per-asset input hashes for the assets the Unity build materialises
/// under <c>Generated/</c> ride along, powering its baked-state skip; staged source
/// files (audio, addition sprites) need none because Unity's own per-bundle hashing
/// sees their stable GUIDs and content directly.
///
/// <para>Bundle files build extensionless into <c>unity_build/</c> (so the addition
/// prefab staging's <c>*.bundle</c> glob never picks them up) and ship as
/// <c>compiled/bundles/&lt;name&gt;.bundle</c>. Names are lowercase because Unity
/// lowercases bundle names on write.</para>
/// </summary>
internal sealed class ReplacementBundlePlan
{
    private const string Header = "jiangyu-bundle-plan 1";

    // Part of every texture hash, so a change in how the Unity pass encodes textures
    // re-bakes them even when their bytes and the toolchain version are unchanged.
    private const string TextureBakePolicy = "additions-dxt-with-mips";

    /// <summary>Extensionless bundle file names this plan produces, sorted.</summary>
    public required IReadOnlyList<string> BundleFiles { get; init; }

    /// <summary>The meshes bundle file name, when the plan carries meshes. The mesh
    /// contract extractor reads this bundle between the two Unity passes.</summary>
    public required string? MeshesBundleFile { get; init; }

    /// <summary>The plan file content the Unity build parses.</summary>
    public required string PlanText { get; init; }

    public static ReplacementBundlePlan Build(
        string bundleName,
        IReadOnlyCollection<string> meshNames,
        IReadOnlyCollection<GlbMeshBundleCompiler.CompiledTexture> textures,
        IReadOnlyCollection<GlbMeshBundleCompiler.ImportedSpriteAsset> sprites,
        IReadOnlyCollection<GlbMeshBundleCompiler.ImportedAudioAsset> audio)
    {
        var mod = bundleName.ToLowerInvariant();
        var bundles = new SortedSet<string>(StringComparer.Ordinal);
        var lines = new List<string> { Header };

        foreach (var clip in audio.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var bundle = GroupedBundleFile(mod, "audio", clip.Name);
            bundles.Add(bundle);
            lines.Add($"audio\t{clip.Name}\t{bundle}");
        }

        if (sprites.Count > 0)
        {
            var bundle = $"{mod}__sprites";
            bundles.Add(bundle);
            lines.Add($"sprites\t{bundle}");
            // Replacement sprites materialise as a Generated/ pair (sprite object plus
            // sprite_source texture), so they carry the input hash the baked-state skip
            // compares. Addition sprites are staged files and need no entry.
            foreach (var sprite in sprites.Where(s => !s.IsAddition).OrderBy(s => s.Name, StringComparer.Ordinal))
                lines.Add($"spritesource\t{sprite.Name}\t{FileFingerprint.Combine(FileFingerprint.OfFile(sprite.SourceFilePath), JiangyuVersion.Current)}");
        }

        foreach (var texture in textures.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var bundle = GroupedBundleFile(mod, "textures", texture.Name);
            bundles.Add(bundle);
            // The Jiangyu version is part of every bake hash: the baked output is a
            // function of the bake code as well as the input bytes, so a Jiangyu upgrade
            // re-bakes everything once rather than serving assets baked by removed logic.
            var role = texture.IsAddition ? "addition" : "replacement";
            var hash = FileFingerprint.Combine(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(texture.Content)),
                texture.Linear ? "linear" : "srgb",
                role,
                TextureBakePolicy,
                JiangyuVersion.Current);
            lines.Add($"texture\t{texture.Name}\t{bundle}\t{hash}\t{role}");
        }

        string? meshesBundle = null;
        if (meshNames.Count > 0)
        {
            meshesBundle = $"{mod}__meshes";
            bundles.Add(meshesBundle);
            lines.Add($"meshes\t{meshesBundle}");
        }

        return new ReplacementBundlePlan
        {
            BundleFiles = [.. bundles],
            MeshesBundleFile = meshesBundle,
            PlanText = string.Join("\n", lines) + "\n",
        };
    }

    // <mod>__<category>__<group> with <mod>__<category> as the fallback for names
    // that carry no group prefix.
    private static string GroupedBundleFile(string mod, string category, string assetName)
    {
        var separator = assetName.IndexOf("__", StringComparison.Ordinal);
        var group = separator > 0 ? assetName[..separator].ToLowerInvariant() : null;
        return group is null ? $"{mod}__{category}" : $"{mod}__{category}__{group}";
    }
}
