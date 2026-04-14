using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Jiangyu.Core.Assets;

public sealed class MeshInspectionService
{
    private const int ClassIdMesh = 43;

    /// <summary>
    /// Inspects all mesh assets matching the given name across a bundle and the game's asset files.
    /// </summary>
    public MeshInspectionReport Inspect(string bundlePath, string gameDataPath, string meshName)
    {
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

        var gameFiles = EnumerateGameAssetFiles(gameDataPath)
            .Select(path => am.LoadAssetsFile(path, loadDeps: false))
            .Where(inst => inst?.file != null)
            .ToList();

        return new MeshInspectionReport
        {
            MeshName = meshName,
            BundlePath = bundlePath,
            GameDataPath = gameDataPath,
            BundleMeshes = [.. bundleAssetFiles.SelectMany(f => FindMeshes(f, meshTemplate, meshName))],
            GameMeshes = [.. gameFiles.SelectMany(f => FindMeshes(f, meshTemplate, meshName))],
        };
    }

    private static IEnumerable<MeshInfo> FindMeshes(AssetsFileInstance inst, AssetTypeTemplateField meshTemplate, string meshName)
    {
        foreach (var info in inst.file.AssetInfos.Where(i => i.TypeId == ClassIdMesh))
        {
            AssetTypeValueField field;
            lock (inst.LockReader)
            {
                field = meshTemplate.MakeValue(inst.file.Reader, info.GetAbsoluteByteOffset(inst.file));
            }

            var name = field["m_Name"].AsString;
            if (!string.Equals(name, meshName, StringComparison.Ordinal))
                continue;

            yield return new MeshInfo
            {
                FilePath = inst.path,
                PathId = info.PathId,
                Name = name,
                BindPoseCount = GetArrayCount(field["m_BindPose"]),
                BindPoseSamples = GetBindPoseSamples(field["m_BindPose"], 2),
                BoneNameHashes = GetUIntArray(field["m_BoneNameHashes"]),
                RootBoneNameHash = field["m_RootBoneNameHash"].IsDummy ? null : field["m_RootBoneNameHash"].AsUInt,
                BonesAabbCount = GetArrayCount(field["m_BonesAABB"]),
                VariableBoneCountWeightsBytes = GetByteArrayLength(field["m_VariableBoneCountWeights.m_Data"]),
                VertexCount = field["m_VertexData.m_VertexCount"].IsDummy ? null : field["m_VertexData.m_VertexCount"].AsUInt,
                ChannelCount = GetArrayCount(field["m_VertexData.m_Channels"]),
                Channels = GetChannelSummaries(field["m_VertexData.m_Channels"]),
                VertexDataSize = GetByteArrayLength(field["m_VertexData.m_DataSize"]),
                VertexSamples = GetVertexSamples(
                    field["m_VertexData.m_Channels"],
                    field["m_VertexData.m_DataSize"],
                    field["m_VertexData.m_VertexCount"].IsDummy ? 0 : (int)field["m_VertexData.m_VertexCount"].AsUInt,
                    3),
            };
        }
    }

    // ── Array / byte helpers ──────────────────────────────────────────────

    private static int GetArrayCount(AssetTypeValueField field)
    {
        if (field.IsDummy)
            return 0;
        var array = field["Array"];
        return array.IsDummy ? 0 : array.Children.Count;
    }

    private static int GetByteArrayLength(AssetTypeValueField field)
    {
        if (field is null || field.IsDummy)
            return 0;
        if (field.Value is not null &&
            field.Value.ValueType == AssetValueType.ByteArray &&
            field.AsByteArray is not null)
            return field.AsByteArray.Length;
        var array = field["Array"];
        return array.IsDummy ? 0 : array.Children.Count;
    }

    private static byte[] GetByteArray(AssetTypeValueField field)
    {
        if (field is null || field.IsDummy)
            return [];
        if (field.Value is not null &&
            field.Value.ValueType == AssetValueType.ByteArray &&
            field.AsByteArray is not null)
            return field.AsByteArray;

        var array = field["Array"];
        if (array.IsDummy)
            return [];

        var bytes = new byte[array.Children.Count];
        for (var i = 0; i < array.Children.Count; i++)
        {
            if (!array.Children[i].IsDummy)
                bytes[i] = array.Children[i].AsByte;
        }

        return bytes;
    }

    private static List<uint> GetUIntArray(AssetTypeValueField field)
    {
        var list = new List<uint>();
        if (field.IsDummy)
            return list;
        var array = field["Array"];
        if (array.IsDummy)
            return list;
        foreach (var child in array.Children)
        {
            if (!child.IsDummy)
                list.Add(child.AsUInt);
        }
        return list;
    }

    // ── Bind pose ─────────────────────────────────────────────────────────

    private static List<string> GetBindPoseSamples(AssetTypeValueField field, int count)
    {
        var result = new List<string>();
        if (field.IsDummy)
            return result;
        var array = field["Array"];
        if (array.IsDummy)
            return result;

        foreach (var child in array.Children.Take(count))
        {
            result.Add(FormatMatrix(child));
        }
        return result;
    }

    private static string FormatMatrix(AssetTypeValueField field)
    {
        string Cell(string name) => field[name].IsDummy ? "?" : field[name].AsFloat.ToString("0.0000");
        var rows = new[]
        {
            $"{Cell("e00")} {Cell("e01")} {Cell("e02")} {Cell("e03")}",
            $"{Cell("e10")} {Cell("e11")} {Cell("e12")} {Cell("e13")}",
            $"{Cell("e20")} {Cell("e21")} {Cell("e22")} {Cell("e23")}",
            $"{Cell("e30")} {Cell("e31")} {Cell("e32")} {Cell("e33")}",
        };
        return "[" + string.Join("; ", rows) + "]";
    }

    // ── Vertex channels ───────────────────────────────────────────────────

    private static List<VertexChannelInfo> ReadChannels(AssetTypeValueField field)
    {
        var result = new List<VertexChannelInfo>();
        if (field is null || field.IsDummy)
            return result;
        var array = field["Array"];
        if (array.IsDummy)
            return result;

        for (var i = 0; i < array.Children.Count; i++)
        {
            var child = array.Children[i];
            if (child.IsDummy)
                continue;

            result.Add(new VertexChannelInfo(
                i,
                child["stream"].IsDummy ? 0 : child["stream"].AsInt,
                child["offset"].IsDummy ? 0 : child["offset"].AsInt,
                child["format"].IsDummy ? 0 : child["format"].AsInt,
                child["dimension"].IsDummy ? 0 : child["dimension"].AsInt));
        }

        return result;
    }

    private static List<string> GetChannelSummaries(AssetTypeValueField field)
    {
        var result = new List<string>();
        if (field is null || field.IsDummy)
            return result;
        var array = field["Array"];
        if (array.IsDummy)
            return result;

        foreach (var child in array.Children)
        {
            if (child.IsDummy)
                continue;

            string Read(string name)
                => child[name].IsDummy ? "?" : child[name].AsInt.ToString();

            result.Add(
                $"stream={Read("stream")} offset={Read("offset")} format={Read("format")} dim={Read("dimension")}");
        }

        return result;
    }

    // ── Vertex decoding ───────────────────────────────────────────────────

    private static List<string> GetVertexSamples(AssetTypeValueField channelsField, AssetTypeValueField dataField, int vertexCount, int count)
    {
        var result = new List<string>();
        if (channelsField is null || channelsField.IsDummy || dataField is null || dataField.IsDummy || vertexCount <= 0)
            return result;

        var rawData = GetByteArray(dataField);
        if (rawData.Length == 0)
            return result;

        var channels = ReadChannels(channelsField);
        if (channels.Count == 0)
            return result;

        var streamStrides = new Dictionary<int, int>();
        foreach (var channel in channels.Where(c => c.Dimension > 0))
        {
            var end = channel.Offset + GetFormatByteWidth(channel.Format) * channel.Dimension;
            if (!streamStrides.TryGetValue(channel.Stream, out var existing) || end > existing)
                streamStrides[channel.Stream] = end;
        }

        var streamOffsets = new Dictionary<int, int>();
        var runningOffset = 0;
        foreach (var stream in streamStrides.Keys.OrderBy(x => x))
        {
            streamOffsets[stream] = runningOffset;
            runningOffset += streamStrides[stream] * vertexCount;
        }

        for (var vertex = 0; vertex < Math.Min(count, vertexCount); vertex++)
        {
            var parts = new List<string>();
            foreach (var channel in channels.Where(c => c.Dimension > 0))
            {
                if (!streamOffsets.TryGetValue(channel.Stream, out var baseOffset))
                    continue;

                var stride = streamStrides[channel.Stream];
                var valueOffset = baseOffset + vertex * stride + channel.Offset;
                parts.Add($"{GetAttributeName(channel.Attribute)}=[{string.Join(",", DecodeChannel(rawData, valueOffset, channel.Format, channel.Dimension))}]");
            }

            result.Add($"v={vertex} {string.Join(" ", parts)}");
        }

        return result;
    }

    private static string[] DecodeChannel(byte[] data, int offset, int format, int dimension)
    {
        var values = new string[dimension];
        var byteWidth = GetFormatByteWidth(format);
        for (var i = 0; i < dimension; i++)
        {
            var valueOffset = offset + i * byteWidth;
            values[i] = format switch
            {
                0 => ReadSingle(data, valueOffset).ToString("0.####"),
                1 => ((float)ReadHalf(data, valueOffset)).ToString("0.####"),
                10 => (valueOffset >= 0 && valueOffset < data.Length ? data[valueOffset] : (byte)0).ToString(),
                _ => $"fmt{format}",
            };
        }

        return values;
    }

    private static float ReadSingle(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            return float.NaN;
        return BitConverter.ToSingle(data, offset);
    }

    private static Half ReadHalf(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
            return (Half)0;
        return BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset));
    }

    private static int GetFormatByteWidth(int format)
        => format switch
        {
            0 => 4,
            1 => 2,
            10 => 1,
            _ => 4,
        };

    private static string GetAttributeName(int attribute)
        => attribute switch
        {
            0 => "Position",
            1 => "Normal",
            2 => "Tangent",
            3 => "Color",
            4 => "UV0",
            5 => "UV1",
            6 => "UV2",
            7 => "UV3",
            8 => "UV4",
            9 => "UV5",
            10 => "UV6",
            11 => "UV7",
            12 => "BlendWeight",
            13 => "BlendIndices",
            _ => $"Attr{attribute}",
        };

    // ── Game asset file enumeration ───────────────────────────────────────

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

// ── Data models ───────────────────────────────────────────────────────────

public sealed class MeshInspectionReport
{
    public string MeshName { get; set; } = string.Empty;
    public string BundlePath { get; set; } = string.Empty;
    public string GameDataPath { get; set; } = string.Empty;
    public List<MeshInfo> BundleMeshes { get; set; } = [];
    public List<MeshInfo> GameMeshes { get; set; } = [];
}

public sealed class MeshInfo
{
    public string FilePath { get; set; } = string.Empty;
    public long PathId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int BindPoseCount { get; set; }
    public List<string> BindPoseSamples { get; set; } = [];
    public List<uint> BoneNameHashes { get; set; } = [];
    public uint? RootBoneNameHash { get; set; }
    public int BonesAabbCount { get; set; }
    public int VariableBoneCountWeightsBytes { get; set; }
    public uint? VertexCount { get; set; }
    public int ChannelCount { get; set; }
    public List<string> Channels { get; set; } = [];
    public int VertexDataSize { get; set; }
    public List<string> VertexSamples { get; set; } = [];
}

public readonly record struct VertexChannelInfo(int Attribute, int Stream, int Offset, int Format, int Dimension);
