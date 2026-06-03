using MelonLoader;

namespace Jiangyu.Loader.Logging;

/// <summary>Routes the SDK host log to the loader's MelonLogger.</summary>
internal sealed class MelonHostLog : IModHostLog
{
    private readonly MelonLogger.Instance _log;

    public MelonHostLog(MelonLogger.Instance log) => _log = log;

    public void Debug(string message) => _log.Msg($"[debug] {message}");

    public void Info(string message) => _log.Msg(message);

    public void Warn(string message) => _log.Warning(message);

    public void Error(string message) => _log.Error(message);
}
