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
    /// for class names that are recognised as Unity assets but whose
    /// authoring path is intentionally deferred — currently <c>Mesh</c>
    /// and <c>GameObject</c>, which wait on the prefab-construction layer
    /// (<c>PREFAB_CLONING_TODO.md</c>) before the addition pipeline can
    /// produce them.
    /// </summary>
    public static string? ForClassName(string className) => className switch
    {
        "Sprite" => Sprites,
        "Texture2D" => Textures,
        "AudioClip" => Audio,
        "Material" => Materials,
        "Mesh" => throw new System.NotSupportedException(
            "Mesh additions are deferred until the prefab-construction layer lands. "
            + "Until then a clone cannot point at a new Mesh from inside a template; "
            + "see PREFAB_CLONING_TODO.md."),
        "GameObject" or "PrefabHierarchyObject" => throw new System.NotSupportedException(
            "Prefab additions are deferred until the prefab-construction layer lands. "
            + "Until then a clone cannot point at a new prefab from inside a template; "
            + "see PREFAB_CLONING_TODO.md."),
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
        "Sprite" or "Texture2D" or "AudioClip" or "Material" => true,
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
}
