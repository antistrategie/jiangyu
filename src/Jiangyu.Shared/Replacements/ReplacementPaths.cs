namespace Jiangyu.Shared.Replacements;

/// <summary>
/// Conventional file-system paths for modder-authored replacement assets,
/// plus parsing for the <c>name--pathId</c> alias scheme. Lives in
/// <c>Jiangyu.Shared</c> because both the compile pipeline (writing paths)
/// and the CLI's asset commands (suggesting paths in <c>jiangyu assets
/// search</c>) need the same conventions, with no game-reference assemblies
/// pulled in.
///
/// <para>The alias scheme: model replacements use <c>name--pathId</c> when
/// disambiguation is needed (multiple <c>PrefabHierarchyObject</c>s and
/// <c>GameObject</c>s can share a name); textures/sprites/audio replacements
/// use a bare <c>name</c> because the loader resolves those by Unity
/// <c>Object.name</c> alone, so the bare name suffices.</para>
/// </summary>
public static class ReplacementPaths
{
    public static string BuildModelReplacementAlias(string targetName, long pathId)
        => $"{targetName}--{pathId}";

    public static string BuildReplacementAlias(string targetName, long? pathId)
        => pathId.HasValue ? $"{targetName}--{pathId.Value}" : targetName;

    public static string BuildModelReplacementRelativePath(string targetName, long pathId, string fileName = "model.gltf")
        => System.IO.Path.Combine("assets", "replacements", "models", BuildModelReplacementAlias(targetName, pathId), fileName)
            .Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public static string BuildModelReplacementRelativePath(string targetName, long? pathId, string fileName = "model.gltf")
        => System.IO.Path.Combine("assets", "replacements", "models", BuildReplacementAlias(targetName, pathId), fileName)
            .Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public static string BuildTextureReplacementRelativePath(string targetName, string extension = ".png")
        => System.IO.Path.Combine("assets", "replacements", "textures", $"{targetName}{NormaliseExtension(extension)}")
            .Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public static string BuildSpriteReplacementRelativePath(string targetName, string extension = ".png")
        => System.IO.Path.Combine("assets", "replacements", "sprites", $"{targetName}{NormaliseExtension(extension)}")
            .Replace(System.IO.Path.DirectorySeparatorChar, '/');

    public static string BuildAudioReplacementRelativePath(string targetName, string extension = ".wav")
        => System.IO.Path.Combine("assets", "replacements", "audio", $"{targetName}{NormaliseExtension(extension)}")
            .Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Parse a replacement alias into a target name and optional pathId.
    /// Accepts both <c>name--pathId</c> and bare <c>name</c> forms. Only
    /// the <c>--&lt;digits&gt;</c> suffix is treated as a pathId; if the
    /// text after <c>--</c> is non-numeric the whole alias is a bare name.
    /// </summary>
    public static bool TryParseReplacementAlias(string alias, out string targetName, out long? pathId)
    {
        targetName = string.Empty;
        pathId = null;

        if (string.IsNullOrWhiteSpace(alias))
            return false;

        var separatorIndex = alias.LastIndexOf("--", System.StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex + 2 < alias.Length &&
            long.TryParse(alias.Substring(separatorIndex + 2), out var parsed))
        {
            targetName = alias.Substring(0, separatorIndex);
            pathId = parsed;
            return true;
        }

        // Bare name — no pathId suffix, or non-numeric suffix after --.
        targetName = alias;
        return true;
    }

    public static bool TryParseModelReplacementAlias(string alias, out string targetName, out long pathId)
    {
        var result = TryParseReplacementAlias(alias, out targetName, out var nullablePathId);
        pathId = nullablePathId ?? 0;
        return result && nullablePathId.HasValue;
    }

    private static string NormaliseExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        return extension.StartsWith('.') ? extension : $".{extension}";
    }
}
