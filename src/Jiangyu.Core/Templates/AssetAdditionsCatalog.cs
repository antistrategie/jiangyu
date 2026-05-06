using Jiangyu.Shared.Replacements;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Compile-time view of <c>assets/additions/&lt;category&gt;/</c> in the mod
/// project. The validator queries this to surface missing-asset errors at
/// compile time rather than at apply time, so a modder learns about a
/// misspelt or unshipped reference before the build hits the loader.
/// </summary>
public interface IAssetAdditionsCatalog
{
    /// <summary>
    /// True when the project ships an asset whose category folder is
    /// <paramref name="category"/> (a value from <see cref="AssetCategory"/>'s
    /// constants) and whose logical name (the file path under that folder
    /// with the extension stripped, slashes preserved) is
    /// <paramref name="name"/>.
    /// </summary>
    bool Contains(string category, string name);
}

/// <summary>
/// File-system implementation of <see cref="IAssetAdditionsCatalog"/>:
/// indexes <c>assets/additions/&lt;category&gt;/**/*</c> at construction so
/// validator queries are O(1).
///
/// The category folders walked are exactly the values declared on
/// <see cref="AssetCategory"/>. Files inside those folders contribute to the
/// catalog by their relative-path stem (the path under the category folder
/// with the file extension stripped, native separators normalised to
/// <c>/</c>). Two files sharing the same stem within the same category — e.g.
/// <c>icon.png</c> and <c>icon.jpg</c> in the same directory — are reported
/// via <see cref="ConflictingNames"/> so the compiler can surface a
/// modder-facing error before the bundle picks one arbitrarily.
/// </summary>
public sealed class FileSystemAssetAdditionsCatalog : IAssetAdditionsCatalog
{
    private static readonly string[] CategoryFolders =
    [
        AssetCategory.Sprites,
        AssetCategory.Textures,
        AssetCategory.Audio,
        AssetCategory.Materials,
    ];

    private readonly Dictionary<string, HashSet<string>> _byCategory =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Names that appeared under more than one file extension in the same
    /// category folder. The compiler should reject these before invoking
    /// the bundle build, since the runtime lookup is by stem and the
    /// disambiguation rule isn't worth specifying.
    /// </summary>
    public List<string> ConflictingNames { get; } = new();

    public FileSystemAssetAdditionsCatalog(string additionRoot)
    {
        if (string.IsNullOrEmpty(additionRoot) || !Directory.Exists(additionRoot))
            return;

        foreach (var category in CategoryFolders)
        {
            var dir = Path.Combine(additionRoot, category);
            if (!Directory.Exists(dir))
                continue;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dir, file);
                var stem = StripExtension(relative).Replace('\\', '/');
                if (!names.Add(stem))
                {
                    ConflictingNames.Add($"{category}/{stem}");
                }
            }

            _byCategory[category] = names;
        }
    }

    public bool Contains(string category, string name)
    {
        if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(name))
            return false;
        return _byCategory.TryGetValue(category, out var set) && set.Contains(name);
    }

    private static string StripExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 0 ? path : path.Substring(0, path.Length - ext.Length);
    }
}
