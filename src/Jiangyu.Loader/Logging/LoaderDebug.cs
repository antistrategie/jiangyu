using Jiangyu.Loader.Diagnostics;
using Jiangyu.Sdk;
using MelonLoader;

namespace Jiangyu.Loader.Logging;

// Single source of truth for the loader's debug-gated logging: the `debug` dev toggle
// and the `[debug]` prefix live here, so LoaderLog, MelonHostLog, and the MelonLogger
// extension share one gate decision rather than three copies.
//
// The loader gate reads the flag per call (DevFlags is refreshed on scene load). The
// SDK's Jiangyu.Sdk.Log keeps a cheap MinLevel filter instead (it drops a Debug line
// before the caller-stack walk that tags it), which SyncSdkLog re-derives from this gate
// on startup and each scene load. Both then honour `debug` and pick up a mid-session
// change at the same point: the next scene load.
internal static class LoaderDebug
{
    public const string Toggle = "debug";

    public static bool Enabled => DevFlags.IsEnabled(Toggle);

    public static string Decorate(string message) => $"[debug] {message}";

    public static void Write(MelonLogger.Instance log, string message)
    {
        if (Enabled)
            log.Msg(Decorate(message));
    }

    // Re-derive the SDK logger's level from this gate. The SDK keeps its own MinLevel
    // filter (so a dropped Debug skips the caller-stack walk); rather than gate its sink
    // per call we re-sync the level whenever the dev flag is re-read.
    public static void SyncSdkLog() => Log.MinLevel = Enabled ? LogLevel.Debug : LogLevel.Info;
}
