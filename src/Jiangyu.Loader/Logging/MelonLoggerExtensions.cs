using MelonLoader;

namespace Jiangyu.Loader.Logging;

// Ergonomic `log.Debug(...)` for the loader subsystems that hold a raw MelonLogger;
// delegates to the shared gate in LoaderDebug. (It is an extension, so were a future
// MelonLoader to add an instance Debug(string) it would shadow this and bypass the gate.
// Acceptable: MelonLogger.Instance has no such method today, and routing every call site
// through LoaderDebug.Write would trade the ergonomics for a hypothetical.)
internal static class MelonLoggerExtensions
{
    public static void Debug(this MelonLogger.Instance log, string message) => LoaderDebug.Write(log, message);
}
