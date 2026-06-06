using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Jiangyu.Core.Deploy;

/// <summary>
/// Detects which loader build is deployed by inspecting the DLL itself rather than
/// trusting a stored setting that could drift from reality. The dev build merges in
/// the diagnostics assembly, so its entry type
/// <c>Jiangyu.Loader.Diagnostics.DevServices</c> is the marker: the lean user build
/// provably lacks it. Reads type definitions only (no assembly load), so it works on a
/// loader DLL that references the game's IL2CPP assemblies the host cannot load.
/// </summary>
public static class LoaderVariantDetector
{
    private const string DevSentinelNamespace = "Jiangyu.Loader.Diagnostics";
    private const string DevSentinelType = "DevServices";
    private const string VersionNamespace = "Jiangyu.Loader";
    private const string VersionType = "BuildInfo";
    private const string VersionField = "Version";

    /// <summary>A loader DLL's variant and build version, read in one metadata pass.</summary>
    public readonly record struct LoaderInfo(string? Variant, string? Version);

    /// <summary>
    /// Inspect a loader DLL in a single pass: its variant (<c>"dev"</c> when the merged
    /// <c>Jiangyu.Loader.Diagnostics.DevServices</c> is present, else <c>"user"</c>) and
    /// the <c>Jiangyu.Loader.BuildInfo.Version</c> constant baked in at build time. Both
    /// are <c>null</c> when the file is absent or not a managed assembly; Version alone
    /// is <c>null</c> when the constant is absent.
    /// </summary>
    public static LoaderInfo Inspect(string loaderDllPath)
    {
        if (string.IsNullOrEmpty(loaderDllPath) || !File.Exists(loaderDllPath))
            return default;
        try
        {
            using var stream = File.OpenRead(loaderDllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return default;

            var reader = pe.GetMetadataReader();
            var variant = "user";
            string? version = null;
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var name = reader.GetString(type.Name);
                var ns = reader.GetString(type.Namespace);

                if (name == DevSentinelType && ns == DevSentinelNamespace)
                    variant = "dev";
                else if (name == VersionType && ns == VersionNamespace)
                    version = ReadVersionConstant(reader, type);
            }
            return new LoaderInfo(variant, version);
        }
        catch
        {
            return default;
        }
    }

    private static string? ReadVersionConstant(MetadataReader reader, TypeDefinition type)
    {
        foreach (var fieldHandle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if (reader.GetString(field.Name) != VersionField)
                continue;

            var constantHandle = field.GetDefaultValue();
            if (constantHandle.IsNil)
                return null;

            var constant = reader.GetConstant(constantHandle);
            if (constant.TypeCode != ConstantTypeCode.String)
                return null;

            return reader.GetBlobReader(constant.Value).ReadConstant(ConstantTypeCode.String) as string;
        }
        return null;
    }

    /// <summary>Inspect the loader deployed at <c>&lt;gameDir&gt;/Mods/Jiangyu.Loader.dll</c>.</summary>
    public static LoaderInfo InspectDeployed(string gameDir) =>
        Inspect(LoaderDeployer.ResolveDestination(gameDir));

    /// <summary>The deployed loader's variant, or <c>null</c> when none is deployed.</summary>
    public static string? DetectDeployed(string gameDir) => InspectDeployed(gameDir).Variant;
}
