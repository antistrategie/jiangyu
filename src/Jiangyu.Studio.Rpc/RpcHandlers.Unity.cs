using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Config;
using Jiangyu.Core.Unity;
using Jiangyu.Shared;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_unity_init",
        "Scaffold unity/ for prefab authoring under the open project. Idempotent: refreshes Jiangyu-managed files and preserves modder content. Returns {createdCount, updatedCount, preservedCount}.")]
    internal static JsonElement UnityInit(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        var scaffolder = new UnityProjectScaffolder(NullLogSink.Instance);
        var result = scaffolder.Init(projectRoot);

        return JsonSerializer.SerializeToElement(new UnityInitResult
        {
            CreatedCount = result.CreatedFiles.Count,
            UpdatedCount = result.OverwrittenFiles.Count,
            PreservedCount = result.PreservedFiles.Count,
        });
    }

    [McpTool("jiangyu_unity_open",
        "Launch Unity Editor on the open project's unity/ subdirectory. Version-matches to the game's Unity install when multiple editors are available. Returns {editorPath, pid}. Requires unity/ to exist (run unity_init first).")]
    internal static JsonElement UnityOpen(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        var unityDir = Path.Combine(projectRoot, "unity");
        if (!Directory.Exists(unityDir))
            throw new InvalidOperationException($"No unity/ project at {unityDir}. Run unity sync first.");

        var config = GlobalConfig.Load();

        // Probe the game's Unity version so editor discovery prefers the
        // matching install when multiple are present. Best-effort: a failed
        // probe falls through to unconstrained discovery rather than aborting.
        string? preferredVersion = null;
        var (gameDataPath, _) = GlobalConfig.ResolveGameDataPath(config);
        if (gameDataPath is not null)
        {
            try
            {
                preferredVersion = UnityVersionValidationService.DetectGameVersion(gameDataPath)?.ToString();
            }
            catch
            {
                // Swallow: preferredVersion stays null and discovery proceeds unconstrained.
            }
        }

        var (editorPath, editorError) = GlobalConfig.ResolveUnityEditorPath(config, preferredVersion);
        if (editorPath is null)
            throw new InvalidOperationException($"Could not resolve Unity Editor: {editorError}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = editorPath,
                Arguments = $"-projectPath \"{unityDir}\"",
                UseShellExecute = false,
            },
        };
        process.Start();

        return JsonSerializer.SerializeToElement(new UnityOpenResult
        {
            EditorPath = editorPath,
            Pid = process.Id,
        });
    }

    [McpTool("jiangyu_unity_import_prefab",
        "Extract a vanilla game prefab into unity/Assets/Imported/<name>/ under the open project. Auto-bootstraps unity/ if missing. Returns {destDir}.")]
    [McpParam("assetName", "string", "Asset name to import (from jiangyu_assets_search).", Required = true)]
    [McpParam("pathId", "integer", "Asset path ID (from jiangyu_assets_search). Use when the name is ambiguous; omit or -1 to resolve by name only.")]
    [McpParam("collection", "string", "Asset collection name (e.g. \"resources.assets\"). Optional narrowing filter.")]
    internal static JsonElement UnityImportPrefab(JsonElement? parameters)
    {
        var assetName = RequireString(parameters, "assetName");
        var pathIdRaw = TryGetLong(parameters, "pathId");
        var collection = TryGetString(parameters, "collection");

        var projectRoot = RpcContext.ProjectRoot
            ?? throw new InvalidOperationException("No project open.");

        // Auto-bootstrap unity/ so import-prefab works as the modder's first
        // unity-namespace action, matching the CLI behaviour.
        if (!Directory.Exists(Path.Combine(projectRoot, "unity")))
        {
            new UnityProjectScaffolder(NullLogSink.Instance).Init(projectRoot);
        }

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var ctx = resolution.Context!;
        var service = ctx.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);

        // Reuse session-cached GameData when available. The cache warms on
        // index build / preview / earlier import, so the second-and-after
        // imports skip the multi-second GameStructure.Load cold start.
        var gameData = EnsureGameData(service, ctx.GameDataPath);

        var destDir = Path.Combine(projectRoot, "unity", "Assets", "Imported", assetName);
        service.ImportPrefabAsUnityAssets(
            gameData,
            assetName: assetName,
            destDir: destDir,
            collection: collection,
            pathId: pathIdRaw ?? -1);

        return JsonSerializer.SerializeToElement(new UnityImportPrefabResult
        {
            DestDir = destDir,
        });
    }

    [RpcType]
    internal sealed class UnityInitResult
    {
        [JsonPropertyName("createdCount")]
        public required int CreatedCount { get; set; }

        [JsonPropertyName("updatedCount")]
        public required int UpdatedCount { get; set; }

        [JsonPropertyName("preservedCount")]
        public required int PreservedCount { get; set; }
    }

    [RpcType]
    internal sealed class UnityOpenResult
    {
        [JsonPropertyName("editorPath")]
        public required string EditorPath { get; set; }

        [JsonPropertyName("pid")]
        public required int Pid { get; set; }
    }

    [RpcType]
    internal sealed class UnityImportPrefabResult
    {
        [JsonPropertyName("destDir")]
        public required string DestDir { get; set; }
    }
}
