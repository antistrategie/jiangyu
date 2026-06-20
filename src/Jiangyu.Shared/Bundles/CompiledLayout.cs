namespace Jiangyu.Shared.Bundles;

/// <summary>
/// Names of the subfolders the compiler writes under a mod's <c>compiled/</c> output and
/// the loader reads from a deployed mod folder. Centralised so the writer (Jiangyu.Core's
/// compile) and the reader (the loader's mod discovery) move together by construction. The
/// compiled template program's filename lives on
/// <see cref="Jiangyu.Shared.Templates.CompiledTemplatePatchManifest.FileName"/>.
/// </summary>
public static class CompiledLayout
{
    /// <summary>Subfolder holding the compiled Unity asset bundles.</summary>
    public const string BundlesDirName = "bundles";

    /// <summary>Subfolder holding the compiled mod code DLL(s).</summary>
    public const string CodeDirName = "code";

    /// <summary>Subfolder holding the compiled per-locale translation tables and source catalogue.</summary>
    public const string LocalesDirName = "locales";
}
