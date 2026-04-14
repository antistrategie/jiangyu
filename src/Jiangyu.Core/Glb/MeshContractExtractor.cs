using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Jiangyu.Core.Glb;

public static class MeshContractExtractor
{
    private const int ClassIdMesh = 43;

    public sealed class MeshContract
    {
        public required string MeshName { get; init; }
        public required uint[] BoneNameHashes { get; init; }
        public required uint RootBoneNameHash { get; init; }
        public required float[][] BindPoses { get; init; }
    }

    public static Dictionary<string, MeshContract> Extract(string bundlePath, string gameDataPath, IEnumerable<string> meshNames)
    {
        var required = new HashSet<string>(meshNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.Ordinal);
        if (required.Count == 0)
            return new Dictionary<string, MeshContract>(StringComparer.Ordinal);

        var am = new AssetsManager
        {
            UseQuickLookup = true,
            UseTemplateFieldCache = true,
        };

        var bundle = am.LoadBundleFile(bundlePath);
        var bundleAssetFiles = new List<AssetsFileInstance>();
        for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            var inst = am.LoadAssetsFileFromBundle(bundle, i, loadDeps: false);
            if (inst?.file != null)
                bundleAssetFiles.Add(inst);
        }

        var templateSource = bundleAssetFiles.FirstOrDefault(f => f.file.Metadata.TypeTreeEnabled)
            ?? throw new InvalidOperationException("Bundle does not contain typetrees for Mesh.");
        var meshTemplateInfo = templateSource.file.AssetInfos.FirstOrDefault(i => i.TypeId == ClassIdMesh)
            ?? throw new InvalidOperationException("Bundle contains no Mesh assets to use as template.");
        var meshTemplate = am.GetTemplateBaseField(templateSource, meshTemplateInfo)
            ?? throw new InvalidOperationException("Failed to get Mesh type template from bundle.");

        var result = new Dictionary<string, MeshContract>(StringComparer.Ordinal);
        foreach (var path in EnumerateGameAssetFiles(gameDataPath))
        {
            if (result.Count == required.Count)
                break;

            var inst = am.LoadAssetsFile(path, loadDeps: false);
            if (inst?.file == null)
                continue;

            foreach (var contract in FindContracts(inst, meshTemplate, required))
                result[contract.MeshName] = contract;
        }

        return result;
    }

    private static IEnumerable<MeshContract> FindContracts(AssetsFileInstance inst, AssetTypeTemplateField meshTemplate, HashSet<string> required)
    {
        foreach (var info in inst.file.AssetInfos.Where(i => i.TypeId == ClassIdMesh))
        {
            AssetTypeValueField field;
            lock (inst.LockReader)
            {
                field = meshTemplate.MakeValue(inst.file.Reader, info.GetAbsoluteByteOffset(inst.file));
            }

            var name = field["m_Name"].AsString;
            if (!required.Contains(name))
                continue;

            yield return new MeshContract
            {
                MeshName = name,
                BoneNameHashes = GetUIntArray(field["m_BoneNameHashes"]),
                RootBoneNameHash = field["m_RootBoneNameHash"].IsDummy ? 0U : field["m_RootBoneNameHash"].AsUInt,
                BindPoses = GetBindPoses(field["m_BindPose"]),
            };
        }
    }

    private static uint[] GetUIntArray(AssetTypeValueField field)
    {
        if (field.IsDummy)
            return [];

        var array = field["Array"];
        if (array.IsDummy)
            return [];

        return array.Children
            .Where(child => !child.IsDummy)
            .Select(child => child.AsUInt)
            .ToArray();
    }

    private static float[][] GetBindPoses(AssetTypeValueField field)
    {
        if (field.IsDummy)
            return [];

        var array = field["Array"];
        if (array.IsDummy)
            return [];

        return array.Children
            .Where(child => !child.IsDummy)
            .Select(child => new[]
            {
                child["e00"].AsFloat, child["e01"].AsFloat, child["e02"].AsFloat, child["e03"].AsFloat,
                child["e10"].AsFloat, child["e11"].AsFloat, child["e12"].AsFloat, child["e13"].AsFloat,
                child["e20"].AsFloat, child["e21"].AsFloat, child["e22"].AsFloat, child["e23"].AsFloat,
                child["e30"].AsFloat, child["e31"].AsFloat, child["e32"].AsFloat, child["e33"].AsFloat,
            })
            .ToArray();
    }

    private static IEnumerable<string> EnumerateGameAssetFiles(string gameDataPath)
    {
        foreach (var path in Directory.EnumerateFiles(gameDataPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("globalgamemanagers.assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("resources.assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }
}
