namespace Jiangyu.Shared.Replacements;

/// <summary>
/// Shared mapping between Unity Object class names and the asset category
/// folder modders use under <c>assets/replacements/&lt;category&gt;/</c> and
/// <c>assets/additions/&lt;category&gt;/</c>.
///
/// <para>The compiler (when validating that an <c>asset="name"</c> reference
/// resolves to a real file) and the loader applier (when resolving the
/// reference at apply time onto a Unity Object-typed template field) both
/// dispatch through this single table. Adding a new category — most
/// imminently <c>Mesh</c> and <c>GameObject</c> for the prefab-cloning
/// work — means changing one place.</para>
///
/// <para>Class names match the Unity Object simple type name. The compiler
/// indexes assets by these names already (see <c>CompilationService</c>'s
/// classification dispatch), so the convention is consistent with how
/// replacements are indexed.</para>
/// </summary>
public static class AssetCategory
{
    public const string Sprites = "sprites";
    public const string Textures = "textures";
    public const string Audio = "audio";
    public const string Materials = "materials";
    public const string Meshes = "models";
    public const string Prefabs = "prefabs";

    /// <summary>
    /// Map a Unity Object simple class name (e.g. <c>"Sprite"</c>) to its
    /// addition/replacement category folder, or null when the class is not
    /// a supported asset kind. Throws <see cref="System.NotSupportedException"/>
    /// for class names that are recognised as Unity assets but cannot be
    /// satisfied by an addition: <c>Mesh</c> (no template field is typed
    /// <c>Mesh</c> in MENACE; ship a prefab addition instead) and
    /// <c>PrefabHierarchyObject</c> (AssetRipper's extraction wrapper, not a
    /// real template field type).
    /// </summary>
    public static string? ForClassName(string className) => className switch
    {
        "Sprite" => Sprites,
        "Texture2D" => Textures,
        "AudioClip" => Audio,
        "Material" => Materials,
        "GameObject" => Prefabs,
        "Mesh" => throw new System.NotSupportedException(
            "Mesh additions are not supported because no template field in MENACE is "
            + "typed Mesh; templates carry GameObject references instead. "
            + "Author a prefab addition (see site/assets/additions/prefabs) that "
            + "wraps the mesh inside a SkinnedMeshRenderer."),
        "PrefabHierarchyObject" => throw new System.NotSupportedException(
            "PrefabHierarchyObject is AssetRipper's extraction wrapper, not a Unity "
            + "Object type that appears on template fields. Asset references targeting "
            + "a PHO are author error: target the backing GameObject instead."),
        _ => null,
    };

    /// <summary>
    /// True when <paramref name="className"/> is a Unity Object class that
    /// the asset-reference path knows how to resolve today. Use this in the
    /// compiler validator and the applier to decide whether an
    /// <c>asset="name"</c> on a field of that type is supported, before
    /// reaching for <see cref="ForClassName"/> (which throws on deferred
    /// kinds).
    /// </summary>
    public static bool IsSupported(string className) => className switch
    {
        "Sprite" or "Texture2D" or "AudioClip" or "Material" or "GameObject" => true,
        _ => false,
    };

    /// <summary>
    /// Translate a modder-facing logical name (the path under
    /// <c>assets/additions/&lt;category&gt;/</c> with slashes preserved) into
    /// the flat name used as the Unity Object's <c>.name</c> inside the
    /// bundle. <c>/</c> escapes to <c>__</c> because Unity's
    /// <c>AssetDatabase.CreateAsset</c> overwrites <c>Object.name</c> to the
    /// asset file's stem, which can't contain real path separators. The
    /// runtime resolver applies the same translation before looking up so
    /// the modder's KDL reference (which keeps slashes) and the bundle name
    /// stay in sync without the modder having to think about it.
    /// </summary>
    public static string ToBundleAssetName(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName))
            return string.Empty;
        return logicalName.Replace("/", "__").Replace("\\", "__");
    }

    /// <summary>
    /// Build the modder-facing logical name for a file under
    /// <c>assets/additions/&lt;category&gt;/</c>: the relative path with the
    /// file extension stripped and platform separators normalised to
    /// <c>/</c>. The compile pipeline (when packing additions into the
    /// bundle) and the studio (when listing project additions for asset
    /// reference dropdowns) both call through here so the same
    /// <c>asset="lrm5/icon"</c> string the modder writes in KDL is what
    /// they see in the picker and what the bundle indexes by.
    /// </summary>
    public static string LogicalAdditionName(string categoryRoot, string filePath)
    {
        var relative = System.IO.Path.GetRelativePath(categoryRoot, filePath);
        var ext = System.IO.Path.GetExtension(relative);
        var stem = ext.Length == 0 ? relative : relative.Substring(0, relative.Length - ext.Length);
        return stem.Replace('\\', '/');
    }

    private static readonly string[] _imageExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] _audioExtensions = [".wav", ".ogg", ".mp3"];
    private static readonly string[] _noExtensions = [];

    /// <summary>
    /// Source-file extensions the compile pipeline knows how to pack as
    /// additions for a given category, lower-cased and dot-prefixed
    /// (<c>.png</c>, <c>.wav</c>). The studio's asset-reference picker
    /// uses the same list so it only suggests files the build will
    /// actually include in the bundle. <c>Materials</c> intentionally
    /// returns an empty list: <c>BundleReplacementCatalog</c> has no
    /// Materials dictionary yet, so a "material addition" file would
    /// be silently dropped at compile time.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> AdditionExtensionsForCategory(string category)
        => category switch
        {
            Sprites => _imageExtensions,
            Textures => _imageExtensions,
            Audio => _audioExtensions,
            Prefabs => _bundleExtensions,
            _ => _noExtensions,
        };

    private static readonly string[] _bundleExtensions = [".bundle"];
}
