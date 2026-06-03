using MelonLoader;

namespace Jiangyu.Loader.Logging;

/// <summary>
/// Wraps the loader's MelonLogger.Instance and tags each line with the mod
/// currently being processed, so a mod-scoped line reads
/// "[Jiangyu] [modId] message". Leave Mod null for loader-global lines.
/// </summary>
internal sealed class LoaderLog
{
    private readonly MelonLogger.Instance _log;

    public LoaderLog(MelonLogger.Instance log) => _log = log;

    public string Mod { get; set; }

    /// <summary>The underlying logger, for subsystems that log without a mod scope.</summary>
    public MelonLogger.Instance Raw => _log;

    public void Msg(string message) => _log.Msg(Format(message));

    public void Warning(string message) => _log.Warning(Format(message));

    public void Error(string message) => _log.Error(Format(message));

    private string Format(string message)
        => string.IsNullOrEmpty(Mod) ? message : $"[{Mod}] {message}";
}
