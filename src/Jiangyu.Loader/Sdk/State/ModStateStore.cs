using System;
using System.IO;
using Jiangyu.Loader.Logging;
using Jiangyu.Shared.State;

namespace Jiangyu.Loader.Sdk.State;

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
            if (context.State is not PersistentModState state)
                continue;
            var sidecar = SidecarPath(savePath, context.ModId);
            try
            {
                if (state.HasState)
                    File.WriteAllText(sidecar, state.Serialize());
                // A mod with nothing to say about this save must leave nothing behind either:
                // whatever sits here belongs to the game that occupied the slot before.
                else if (File.Exists(sidecar))
                    File.Delete(sidecar);
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
                else
                {
                    // No sidecar for this save: clear so a prior session's state does not leak into a
                    // save that predates the mod (or never had state).
                    state.Clear();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"mod state: failed to load {context.ModId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Delete every mod's sidecar beside <paramref name="savePath"/>, for a save the game
    /// is dropping. Sweeps the folder rather than the loaded mods, so a sidecar left by a mod that
    /// is disabled this session goes with the save it belonged to.</summary>
    public void DeleteAll(string savePath)
    {
        if (string.IsNullOrEmpty(savePath))
            return;

        var folder = Path.GetDirectoryName(savePath);
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            // Names, not paths: the two spellings of the folder need not agree character for
            // character, and every sidecar for this save sits right beside it.
            var saveFile = Path.GetFileName(savePath);
            foreach (var sidecar in Directory.GetFiles(folder, ModStateSidecar.SearchPattern))
            {
                if (ModStateSidecar.SavePathFor(Path.GetFileName(sidecar)) != saveFile)
                    continue;
                try
                {
                    File.Delete(sidecar);
                }
                catch (Exception ex)
                {
                    _log.Error($"mod state: failed to delete {Path.GetFileName(sidecar)}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: failed to sweep sidecars for {Path.GetFileName(savePath)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Drop every mod's state, e.g. when a new game starts (which never triggers a load),
    /// so nothing carries over from the previous session.</summary>
    public void ResetAll()
    {
        foreach (var context in _host.Contexts)
            (context.State as PersistentModState)?.Clear();
    }

    private static string SidecarPath(string savePath, string modId)
        => ModStateSidecar.PathFor(savePath, modId);
}
