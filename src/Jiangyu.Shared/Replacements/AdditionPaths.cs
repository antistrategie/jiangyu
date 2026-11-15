namespace Jiangyu.Shared.Replacements;

/// <summary>
/// Where a mod project keeps its asset additions. A category folder sits
/// directly under <c>assets/</c> (<c>assets/sprites/</c>) or under
/// <c>assets/additions/</c> (<c>assets/additions/sprites/</c>). Both are read,
/// and a file's logical name is its path under the category folder either way.
/// The compiler and the Studio handlers both resolve folders through here so
/// they agree on what a project ships.
/// </summary>
public static class AdditionPaths
{
    private static readonly string[] Categories =
    {
        AssetCategory.Sprites,
        AssetCategory.Textures,
        AssetCategory.Audio,
        AssetCategory.Materials,
        AssetCategory.Prefabs,
        AssetCategory.Meshes,
    };

    /// <summary>
    /// The folders whose category subfolders hold additions:
    /// <c>assets/</c> first, then <c>assets/additions/</c>.
    /// </summary>
    public static string[] Roots(string projectDir) => new[]
    {
        System.IO.Path.Combine(projectDir, "assets"),
        System.IO.Path.Combine(projectDir, "assets", "additions"),
    };

    /// <summary>The folders that hold additions of <paramref name="category"/>.</summary>
    public static string[] CategoryDirs(string projectDir, string category)
    {
        var roots = Roots(projectDir);
        var dirs = new string[roots.Length];
        for (var i = 0; i < roots.Length; i++)
            dirs[i] = System.IO.Path.Combine(roots[i], category);
        return dirs;
    }

    /// <summary>
    /// Every folder an addition file can sit in: the whole of
    /// <c>assets/additions/</c>, plus each category folder directly under
    /// <c>assets/</c>. <c>assets/</c> itself is never one, since it also
    /// holds <c>assets/replacements/</c>.
    /// </summary>
    public static string[] ScanDirs(string projectDir)
    {
        var dirs = new string[Categories.Length + 1];
        dirs[0] = System.IO.Path.Combine(projectDir, "assets", "additions");
        for (var i = 0; i < Categories.Length; i++)
            dirs[i + 1] = System.IO.Path.Combine(projectDir, "assets", Categories[i]);
        return dirs;
    }
}
