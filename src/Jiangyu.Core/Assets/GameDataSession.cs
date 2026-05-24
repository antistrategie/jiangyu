using System.Security.Cryptography;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using AssetRipper.Import.Logging;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Assets;

/// <summary>
/// One-shot scope around an AssetRipper <see cref="GameStructure"/>: loads
/// the game data, registers the progress-sink adapter on <see cref="Logger"/>,
/// runs the standard processor pipeline, and disposes both on exit. Replaces
/// the six near-identical <c>GameStructure.Load + RunProcessors</c> envelopes
/// that previously lived in each Core service.
///
/// <para>Construction is the load; the session is ready for use immediately
/// after. Wrap in a <c>using</c> so the structure and adapter are released
/// when work completes. <see cref="HasAnyAssetCollections"/> tells the caller
/// whether the load found anything before they dig in further.</para>
/// </summary>
public sealed class GameDataSession : IDisposable
{
    public GameData GameData { get; }

    private readonly GameStructure _structure;
    private readonly AssetRipperProgressAdapter? _adapter;

    public GameDataSession(
        string gameDataPath,
        IProgressSink? progress = null,
        bool includeSpriteProcessor = false,
        ScriptContentLevel scriptContentLevel = ScriptContentLevel.Level2)
    {
        if (progress is not null)
        {
            _adapter = new AssetRipperProgressAdapter(progress);
            Logger.Add(_adapter);
        }

        progress?.SetPhase("Loading assets");
        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = scriptContentLevel;

        _structure = GameStructure.Load([gameDataPath], LocalFileSystem.Instance, settings);
        GameData = GameData.FromGameStructure(_structure);

        progress?.SetPhase("Processing");
        RunProcessors(GameData, includeSpriteProcessor);
        progress?.Finish();
    }

    public bool HasAnyAssetCollections => GameData.GameBundle.HasAnyAssetCollections();

    public void Dispose()
    {
        if (_adapter is not null)
            Logger.Remove(_adapter);
        _structure.Dispose();
    }

    private static void RunProcessors(GameData gameData, bool includeSpriteProcessor)
    {
        var processors = new List<IAssetProcessor>
        {
            new SceneDefinitionProcessor(),
            new MainAssetProcessor(),
            new AnimatorControllerProcessor(),
            new PrefabProcessor(),
        };

        // SpriteProcessor is needed only by the asset-index pipeline; other
        // callers (template index, structural baseline, inspection) don't
        // touch sprite assets and skip the cost.
        if (includeSpriteProcessor)
            processors.Add(new AssetRipper.Processing.Textures.SpriteProcessor());

        foreach (var processor in processors)
            processor.Process(gameData);
    }

    /// <summary>
    /// SHA-256 of the platform-specific <c>GameAssembly</c> binary. Used as
    /// the cache-staleness key by every service that caches game-derived
    /// data; if the IL2CPP build changes, every cache invalidates atomically.
    /// Returns null when no <c>GameAssembly.so</c>/<c>.dll</c> sits beside
    /// <paramref name="gameDataPath"/>.
    /// </summary>
    public static string? ComputeGameAssemblyHash(string gameDataPath)
    {
        var parent = Path.GetDirectoryName(gameDataPath);
        if (string.IsNullOrEmpty(parent))
            return null;

        var candidates = new[]
        {
            Path.Combine(parent, "GameAssembly.so"),
            Path.Combine(parent, "GameAssembly.dll"),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            using var stream = File.OpenRead(candidate);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        return null;
    }
}
