using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Config;

public sealed class EnvironmentContext
{
    public required GlobalConfig Config { get; init; }
    public required string GameDataPath { get; init; }
    public required string CachePath { get; init; }
    public string? UnityEditorPath { get; init; }

    public static EnvironmentContextResolution ResolveFromGlobalConfig()
    {
        return ResolveFromConfig(GlobalConfig.Load());
    }

    public static EnvironmentContextResolution ResolveFromConfig(GlobalConfig config)
    {
        var (gameDataPath, error) = GlobalConfig.ResolveGameDataPath(config);
        if (gameDataPath is null)
        {
            return new EnvironmentContextResolution
            {
                Error = error ?? "Could not resolve game data path.",
            };
        }

        return new EnvironmentContextResolution
        {
            Context = new EnvironmentContext
            {
                Config = config,
                GameDataPath = gameDataPath,
                CachePath = config.GetCachePath(),
                UnityEditorPath = string.IsNullOrWhiteSpace(config.UnityEditor)
                    ? null
                    : GlobalConfig.ExpandHome(config.UnityEditor),
            },
        };
    }

    public AssetPipelineService CreateAssetPipelineService(IProgressSink progress, ILogSink log)
        => new(GameDataPath, CachePath, progress, log);

    public TemplateIndexService CreateTemplateIndexService(IProgressSink progress, ILogSink log)
        => new(GameDataPath, CachePath, progress, log);

    public ObjectInspectionService CreateObjectInspectionService(IProgressSink progress, ILogSink log)
        => new(GameDataPath, CachePath, progress, log);
}

public sealed class EnvironmentContextResolution
{
    public EnvironmentContext? Context { get; init; }
    public string? Error { get; init; }
    public bool Success => Context is not null;
}
