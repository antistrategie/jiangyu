using System;
using System.IO;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Persists every mod's <see cref="PersistentModState"/> to a per-save-slot sidecar
/// next to the game's save file (<c>&lt;savePath&gt;.jiangyu.&lt;modId&gt;.json</c>),
/// keyed by the save path so state never leaks across slots. Driven by the save/load
/// Harmony hooks; one bad mod's serialisation is logged and never blocks the others.
/// </summary>
internal sealed class ModStateStore
{
    private readonly ModHost _host;
    private readonly IModHostLog _log;

    public ModStateStore(ModHost host, IModHostLog log)
    {
        _host = host;
        _log = log;
    }

    /// <summary>Write each mod's state to its sidecar beside <paramref name="savePath"/>.</summary>
    public void WriteAll(string savePath)
    {
        if (string.IsNullOrEmpty(savePath))
            return;

        foreach (var context in _host.Contexts)
        {
            if (context.State is not PersistentModState state || !state.HasState)
                continue;
            try
            {
                File.WriteAllText(SidecarPath(savePath, context.ModId), state.Serialize());
            }
            catch (Exception ex)
            {
                _log.Error($"mod state: failed to write {context.ModId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Load each mod's state from its sidecar beside <paramref name="savePath"/>.</summary>
    public void LoadAll(string savePath)
    {
        if (string.IsNullOrEmpty(savePath))
            return;

        foreach (var context in _host.Contexts)
        {
            if (context.State is not PersistentModState state)
                continue;
            var sidecar = SidecarPath(savePath, context.ModId);
            try
            {
                if (File.Exists(sidecar))
                {
                    state.Load(File.ReadAllText(sidecar));
                    _log.Info($"mod state: loaded {context.ModId}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"mod state: failed to load {context.ModId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string SidecarPath(string savePath, string modId)
        => $"{savePath}.jiangyu.{modId}.json";
}
