using Jiangyu.Core.Config;
using Jiangyu.Core.Templates;

namespace Jiangyu.Core.Code;

/// <summary>
/// Bridges a project's compiled code DLLs into a <see cref="TemplateTypeCatalog"/>:
/// where the DLLs live, how the catalog resolves a mod <c>[JiangyuType]</c> referenced
/// by name, and how to name one as a <c>type=</c> construction choice. One place so the
/// template query handler and the CLI agree on the <c>compiled/code</c> layout and the
/// <c>modId:Name</c> convention without each loading the code twice.
/// </summary>
public static class CodeTypeResolver
{
    /// <summary>
    /// The compiled code assemblies under <paramref name="codeDirectory"/> and the
    /// extra catalog search directories their references need, for feeding
    /// <see cref="TemplateTypeCatalog.Load"/>. Both empty when there is no built code.
    /// </summary>
    public static (IReadOnlyList<string> Assemblies, IReadOnlyList<string> SearchDirectories) LoadInputs(
        string codeDirectory)
    {
        if (!Directory.Exists(codeDirectory))
            return ([], []);

        var assemblies = Directory.GetFiles(codeDirectory, "*.dll");
        if (assemblies.Length == 0)
            return ([], []);

        var (sdkDir, _) = GlobalConfig.ResolveSdkDir(GlobalConfig.Load());
        var searchDirectories = new List<string> { codeDirectory };
        if (sdkDir is not null)
            searchDirectories.Add(sdkDir);

        return (assemblies, searchDirectories);
    }

    /// <summary>
    /// The bare name in a <c>modId:Name</c> reference (the part after the first
    /// <c>:</c>), or the whole string when it carries no <c>:</c>.
    /// </summary>
    public static string BareName(string qualifiedName)
    {
        var colon = qualifiedName.IndexOf(':');
        return colon >= 0 ? qualifiedName[(colon + 1)..] : qualifiedName;
    }

    /// <summary>
    /// The CLR <c>FullName</c> of the scanned code <c>[JiangyuType]</c> whose bare name
    /// (the part after <c>modId:</c>) matches, or null when none does.
    /// </summary>
    public static string? ResolveFullName(TemplateTypeCatalog catalog, string bareName)
    {
        foreach (var type in catalog.ScannedExtraTypes)
            if (string.Equals(JiangyuTypeMetadataReader.BareName(type), bareName, StringComparison.Ordinal))
                return type.FullName;
        return null;
    }

    /// <summary>
    /// The KDL <c>type=</c> reference (<c>modId:Name</c>) for a scanned code
    /// <c>[JiangyuType]</c>, or null when <paramref name="type"/> is a game type (named
    /// by the catalog instead) or no mod id is known.
    /// </summary>
    public static string? QualifiedName(TemplateTypeCatalog catalog, Type type, string? modId)
    {
        if (modId is null || !catalog.ScannedExtraTypes.Contains(type))
            return null;
        var bareName = JiangyuTypeMetadataReader.BareName(type);
        return bareName is null ? null : $"{modId}:{bareName}";
    }
}
