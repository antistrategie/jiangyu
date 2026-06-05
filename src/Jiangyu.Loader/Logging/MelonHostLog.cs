using MelonLoader;

namespace Jiangyu.Loader.Logging;

/// <summary>Routes the SDK host log to the loader's MelonLogger.</summary>
internal sealed class MelonHostLog : IModHostLog
{
    private readonly MelonLogger.Instance _log;

    public MelonHostLog(MelonLogger.Instance log) => _log = log;

    // Verbose detail, emitted only when the `debug` dev flag is set (see LoaderDebug), so
    // it stays out of a normal play log.
    public void Debug(string message) => LoaderDebug.Write(_log, message);

    public void Info(string message) => _log.Msg(message);

    public void Warn(string message) => _log.Warning(message);

    public void Error(string message) => _log.Error(message);
}
