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
    [McpTool("jiangyu_templates_index_status",
        "Check whether the MENACE template index exists and is current. Returns state (\"current\", \"noGame\", \"stale\", etc.), instance/type counts, and indexed-at timestamp.")]
    internal static JsonElement TemplatesIndexStatus(JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            return JsonSerializer.SerializeToElement(new TemplateIndexStatus
            {
                State = "noGame",
                Reason = resolution.Error,
            });
        }

        var service = RpcHelpers.RequireEnvironment().CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var status = service.GetIndexStatus();
        var manifest = service.LoadManifest();

        return JsonSerializer.SerializeToElement(new TemplateIndexStatus
        {
            State = status.State switch
            {
                CachedIndexState.Current => "current",
                CachedIndexState.Stale => "stale",
                CachedIndexState.Missing => "missing",
                _ => "missing",
            },
            Reason = status.Reason,
            InstanceCount = manifest?.InstanceCount,
            TypeCount = manifest?.TemplateTypeCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }

    [McpTool("jiangyu_templates_index",
        "Rebuild the MENACE template index. Required before template search/query/inspect work. Long-running; waits until complete.")]
    internal static JsonElement TemplatesIndex(JsonElement? __)
    {
        var ctx = RpcHelpers.RequireEnvironment();
        ClearIndexCaches();

        var service = ctx.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        service.BuildIndex();

        // Build supplement AFTER template index — AssetRipper Level2 initialises
        // LibCpp2IL first, then Cpp2IL can register instruction sets idempotently.
        BuildSupplementIfStale(ctx);

        var manifest = service.LoadManifest();
        return JsonSerializer.SerializeToElement(new TemplateIndexStatus
        {
            State = "current",
            InstanceCount = manifest?.InstanceCount,
            TypeCount = manifest?.TemplateTypeCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }
    private static void ClearIndexCaches()
    {
        _cachedIndex = null;
        _cachedIndexPath = null;
        _cachedIndexMtime = default;
        _cachedInstantiatedClassNames.Clear();
        _cachedInstanceLookup.Clear();
        _cachedValues = null;
        _cachedValuesPath = null;
        _cachedValuesMtime = default;
        _cachedOdinMatrixSchemas.Clear();
        _prototypeCandidatesCache.Clear();
    }

    // Rebuilds the (templateType → fieldName → schema) registry by joining
    // the index against the values cache and scanning each instance's
    // top-level fields for kind=matrix nodes. First instance wins per
    // (type, field); two instances disagreeing on shape would be a bug
    // worth investigating from source mode anyway.
}
