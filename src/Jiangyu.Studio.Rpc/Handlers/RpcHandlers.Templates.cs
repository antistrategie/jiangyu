using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    private static readonly string DefaultAssemblyRelativePath =
        Path.Combine("MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll");
    private static readonly string MelonLoaderNet6RelativePath =
        Path.Combine("MelonLoader", "net6");






    /// </summary>

    private static TemplateIndex? _cachedIndex;
    private static string? _cachedIndexPath;
    private static DateTime _cachedIndexMtime;
    private static Dictionary<string, List<InspectedFieldNode>>? _cachedValues;
    private static string? _cachedValuesPath;
    private static DateTime _cachedValuesMtime;

    // Derived projections rebuilt alongside _cachedIndex so per-RPC reads stay
    // O(1): membership lookup for the polymorphism heuristic, and
    // (className, name)-keyed instance lookup for templatesValue. Without
    // these the visual editor pays an O(instances) scan per NodeCard mount.
    private static HashSet<string> _cachedInstantiatedClassNames = new(StringComparer.Ordinal);
    private static Dictionary<(string ClassName, string Name), TemplateInstanceEntry> _cachedInstanceLookup = new();

    // Per-(templateType, fieldName) schema for Odin-routed multi-dim
    // arrays, derived by scanning the values cache for kind=matrix nodes.
    // The catalog itself sees these fields as Il2CppObjectBase; this
    // registry lets templatesQuery surface the real element type and
    // representative dimensions so the visual editor can render a grid
    // even for templates whose vanilla value is null (Sirenix Odin omits
    // defaults from the wire format, so a single populated instance per
    // type is enough to recover the shape for all instances).
    private sealed record OdinMatrixFieldSchema(
        int Rank,
        IReadOnlyList<int> Dimensions,
        string? ElementTypeName,
        string ElementKind);

    private static Dictionary<string, Dictionary<string, OdinMatrixFieldSchema>> _cachedOdinMatrixSchemas
        = new(StringComparer.Ordinal);

    private static void RebuildOdinMatrixRegistry()
    {
        _cachedOdinMatrixSchemas.Clear();
        if (_cachedIndex is null || _cachedValues is null) return;

        foreach (var instance in _cachedIndex.Instances)
        {
            var key = TemplateIndex.IdentityKey(instance.Identity);
            if (!_cachedValues.TryGetValue(key, out var fields)) continue;

            foreach (var field in fields)
            {
                if (!string.Equals(field.Kind, "matrix", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(field.Name)) continue;

                if (!_cachedOdinMatrixSchemas.TryGetValue(instance.ClassName, out var perType))
                {
                    perType = new Dictionary<string, OdinMatrixFieldSchema>(StringComparer.Ordinal);
                    _cachedOdinMatrixSchemas[instance.ClassName] = perType;
                }
                if (perType.ContainsKey(field.Name)) continue;

                perType[field.Name] = BuildMatrixSchema(field);
            }
        }
    }

    private static OdinMatrixFieldSchema BuildMatrixSchema(InspectedFieldNode field)
    {
        var dims = field.Dimensions ?? new List<int>();
        var elementKind = field.Elements is { Count: > 0 } els
            ? els[0]?.Kind ?? "scalar"
            : "scalar";
        return new OdinMatrixFieldSchema(
            Rank: dims.Count,
            Dimensions: dims,
            ElementTypeName: ExtractMatrixElementTypeName(field.FieldTypeName),
            ElementKind: elementKind);
    }

    private static string? ExtractMatrixElementTypeName(string? fieldTypeName)
    {
        if (string.IsNullOrEmpty(fieldTypeName)) return null;
        // Strip a trailing `[]`, `[,]`, `[,,]` etc.
        var stripped = System.Text.RegularExpressions.Regex.Replace(fieldTypeName, @"\[,*\]$", "");
        var lastDot = stripped.LastIndexOf('.');
        return lastDot >= 0 ? stripped[(lastDot + 1)..] : stripped;
    }

    // Reloads <see cref="_cachedIndex"/> from disk when the file's mtime
    // diverges from the cached one (handles CLI rebuilds during a Studio
    // session). Also rebuilds the derived class-name set and instance-lookup
    // caches so callers can read them directly. Returns the cached index
    // (possibly null when the index file doesn't exist yet).
    private static TemplateIndex? EnsureIndexCached(EnvironmentContext ctx)
    {
        var service = ctx.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var cachePath = ctx.CachePath;
        DateTime indexMtime;
        try { indexMtime = File.GetLastWriteTimeUtc(service.IndexPath); }
        catch { return _cachedIndex; }

        if (_cachedIndex is not null && _cachedIndexPath == cachePath && _cachedIndexMtime == indexMtime)
            return _cachedIndex;

        _cachedIndex = service.LoadIndex();
        _cachedIndexPath = cachePath;
        _cachedIndexMtime = indexMtime;
        _cachedInstantiatedClassNames.Clear();
        _cachedInstanceLookup.Clear();
        if (_cachedIndex is not null)
        {
            foreach (var instance in _cachedIndex.Instances)
            {
                _cachedInstantiatedClassNames.Add(instance.ClassName);
                _cachedInstanceLookup[(instance.ClassName, instance.Name)] = instance;
            }
        }
        RebuildOdinMatrixRegistry();
        return _cachedIndex;
    }

    private static DateTime _cachedDiscriminatorIndexMtime;
    private static string? _cachedDiscriminatorIndexPath;

    private static void EnsureTaggedDiscriminatorsInstalled(EnvironmentContext ctx)
    {
        var service = ctx.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var indexPath = Path.Combine(ctx.CachePath, "asset-index.json");
        DateTime indexMtime;
        try { indexMtime = File.GetLastWriteTimeUtc(indexPath); }
        catch { return; }

        if (_cachedDiscriminatorIndexPath == indexPath
            && _cachedDiscriminatorIndexMtime == indexMtime
            && TaggedDiscriminatorIndex.IsInstalled)
        {
            return;
        }

        var assetIndex = service.LoadIndex();
        TaggedDiscriminatorIndex.Install(assetIndex?.TaggedDiscriminators);
        _cachedDiscriminatorIndexPath = indexPath;
        _cachedDiscriminatorIndexMtime = indexMtime;
    }

    // Reloads <see cref="_cachedValues"/> from disk when the file's mtime
    // diverges from the cached one. Returns the cached values dictionary
    // (possibly null when no values file exists yet).
    private static Dictionary<string, List<InspectedFieldNode>>? EnsureValuesCached(EnvironmentContext ctx)
    {
        var service = ctx.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var cachePath = ctx.CachePath;
        DateTime valuesMtime;
        try { valuesMtime = File.GetLastWriteTimeUtc(service.ValuesPath); }
        catch { return _cachedValues; }

        if (_cachedValues is not null && _cachedValuesPath == cachePath && _cachedValuesMtime == valuesMtime)
            return _cachedValues;

        _cachedValues = service.LoadValues();
        _cachedValuesPath = cachePath;
        _cachedValuesMtime = valuesMtime;
        RebuildOdinMatrixRegistry();
        return _cachedValues;
    }

    [McpTool("jiangyu_templates_inspect",
        "Inspect a specific template instance's full structure and current vanilla values. Returns the object's fields, options, and nested values. Use the collection and pathId from jiangyu_templates_search results. For [NamedArray(typeof(T))] fields each array element carries its paired enum-member name on the element node (so InitialAttributes[4] reads as {\"name\": \"Vitality\", \"value\": 70}); no follow-up enum lookup is needed.")]
    [McpParam("collection", "string", "Asset collection name (e.g. \"resources.assets\").", Required = true)]
    [McpParam("pathId", "integer", "PathID of the template instance (Int64).", Required = true)]
    internal static JsonElement TemplatesInspect(JsonElement? parameters)
    {
        var collection = RequireString(parameters, "collection");
        var pathId = RequireLong(parameters, "pathId");

        var values = EnsureValuesCached(RpcHelpers.RequireEnvironment());

        List<InspectedFieldNode> fields;
        if (values is not null)
        {
            var key = $"{collection}:{pathId}";
            fields = values.TryGetValue(key, out var cached) ? cached : [];
        }
        else
        {
            // Fall back to live inspection if values cache is missing.
            var inspectionService = RpcHelpers.RequireEnvironment().CreateObjectInspectionService(
        NullProgressSink.Instance, NullLogSink.Instance);
            var result = inspectionService.Inspect(new ObjectInspectionRequest
            {
                Collection = collection,
                PathId = pathId,
                MaxDepth = 4,
                MaxArraySampleLength = 0,
            });
            var structure = result.Fields.FirstOrDefault(f =>
        string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
            fields = structure?.Fields ?? result.Fields;
            return JsonSerializer.SerializeToElement(result, InspectJsonOptions);
        }

        // Return m_Structure fields directly — the frontend looks for m_Structure
        // and falls back to the raw list, so pre-extracting is transparent.
        return JsonSerializer.SerializeToElement(new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = $"template ({collection})",
                ClassName = "Template",
                Collection = collection,
                PathId = pathId,
            },
            Options = new ObjectInspectionOptions
            {
                MaxDepth = 0,
                MaxArraySampleLength = 0,
                Truncated = false,
            },
            Fields = fields,
        },
        InspectJsonOptions);
    }

    [McpTool("jiangyu_templates_value",
        "Read field values from a specific template instance by type name and id. Returns {\"found\": true, \"fields\": [...]} with the instance's field tree. Same shape as jiangyu_templates_inspect — [NamedArray(typeof(T))] elements carry the paired enum-member name on each slot.")]
    [McpParam("typeName", "string", "Template type name (e.g. \"EntityTemplate\").", Required = true)]
    [McpParam("id", "string", "Template instance ID (e.g. \"player_squad.darby\").", Required = true)]
    internal static JsonElement TemplatesValue(JsonElement? parameters)
    {
        var typeName = RequireString(parameters, "typeName");
        var id = RequireString(parameters, "id");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            // No game configured: caller falls back to neutral defaults.
            return JsonSerializer.SerializeToElement(
        new TemplateValueResult { Found = false, Fields = [] }, InspectJsonOptions);
        }

        var ctx = RpcHelpers.RequireEnvironment();
        if (EnsureIndexCached(ctx) is null
            || !_cachedInstanceLookup.TryGetValue((typeName, id), out var instance))
        {
            return JsonSerializer.SerializeToElement(
        new TemplateValueResult { Found = false, Fields = [] }, InspectJsonOptions);
        }

        var values = EnsureValuesCached(ctx);
        var key = TemplateIndex.IdentityKey(instance.Identity);
        var fields = (values is not null && values.TryGetValue(key, out var cached)) ? cached : [];

        return JsonSerializer.SerializeToElement(
            new TemplateValueResult { Found = fields.Count > 0, Fields = fields },
            InspectJsonOptions);
    }

    public static void PreloadTemplateCaches()
    {
        try
        {
            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success) return;
            EnsureIndexCached(RpcHelpers.RequireEnvironment());
            EnsureValuesCached(RpcHelpers.RequireEnvironment());
        }
        catch
        {
            // Caches will load on first RPC call instead.
        }
    }

    private static void BuildSupplementIfStale(EnvironmentContext ctx)
    {
        var gameRoot = Path.GetDirectoryName(ctx.GameDataPath);
        if (gameRoot is null) return;

        var gameAssemblyPath = Path.Combine(gameRoot, "GameAssembly.so");
        if (!File.Exists(gameAssemblyPath))
            gameAssemblyPath = Path.Combine(gameRoot, "GameAssembly.dll");
        if (!File.Exists(gameAssemblyPath)) return;

        var metadataPath = Path.Combine(ctx.GameDataPath, "il2cpp_data", "Metadata", "global-metadata.dat");
        if (!File.Exists(metadataPath)) return;

        if (Il2CppMetadataCache.LoadIfFresh(ctx.CachePath, gameAssemblyPath, metadataPath) is not null)
            return;

        var unityVersion = GetGameUnityVersionCached(ctx.GameDataPath);
        if (unityVersion is null) return;

        Il2CppMetadataCache.BuildAndPersist(ctx.CachePath, gameAssemblyPath, metadataPath, unityVersion.Value, NullLogSink.Instance);
    }
}
