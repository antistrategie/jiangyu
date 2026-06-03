using System;
using System.Diagnostics;
using System.Reflection;

namespace Jiangyu.Sdk;

/// <summary>Severity for <see cref="Log"/>, ordered Debug &lt; Info &lt; Warn &lt; Error.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Process-wide logger for mod code that has no <see cref="ModContext"/> — injected
/// <c>[JiangyuType]</c> handlers the game constructs, or static helpers. The loader
/// binds the sink and the minimum level at startup. <see cref="Debug"/> is dropped
/// unless a dev opts in (the loader raises the level), so debug logging can stay in
/// a shipped mod without spamming the player's log or being stripped before release.
///
/// Every line is tagged with the mod that emitted it, resolved from the call stack
/// (the bound mod resolver maps the calling assembly to its mod id) — the same
/// <c>[modId]</c> tag <c>Context.Log</c> applies, so both logging paths look alike.
/// Resolution runs only after the level check, so a dropped <see cref="Debug"/>
/// costs nothing.
/// </summary>
public static class Log
{
    private static Action<LogLevel, string> _sink = static (_, _) => { };
    private static Func<Assembly, string> _modResolver = static _ => null;

    /// <summary>Messages below this level are dropped. The loader sets it: Info by
    /// default, Debug when the dev opts in.</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Route log output into the host. Called by the loader at startup.</summary>
    public static void Bind(Action<LogLevel, string> sink) => _sink = sink ?? (static (_, _) => { });

    /// <summary>
    /// Bind the calling-assembly-to-mod-id resolver so lines are auto-tagged with the
    /// emitting mod. Called by the loader at startup. Until bound (or for a line with
    /// no mod-owned frame, e.g. loader internals) lines are emitted untagged.
    /// </summary>
    public static void BindModResolver(Func<Assembly, string> resolver) => _modResolver = resolver ?? (static _ => null);

    public static void Debug(string message) => Emit(LogLevel.Debug, message);

    public static void Info(string message) => Emit(LogLevel.Info, message);

    public static void Warn(string message) => Emit(LogLevel.Warn, message);

    public static void Error(string message) => Emit(LogLevel.Error, message);

    private static void Emit(LogLevel level, string message)
    {
        if (level < MinLevel)
            return;

        var mod = ResolveCallerMod();
        _sink(level, mod == null ? message : $"[{mod}] {message}");
    }

    // The mod id of the nearest frame whose assembly the host recognises as a mod.
    // A handler's own frame is in its mod assembly, so the immediate caller resolves;
    // walking past unrecognised frames (the SDK, the loader, wrappers) is robust to
    // inlining of the immediate caller. Frames are taken one at a time from the caller
    // (skipping this method and Emit) and the walk stops at the first match, so the
    // common case resolves in a frame or two without capturing the whole stack.
    private static string ResolveCallerMod()
    {
        const int maxFrames = 16;
        for (var depth = 2; depth < 2 + maxFrames; depth++)
        {
            var method = new StackFrame(depth, needFileInfo: false).GetMethod();
            if (method == null)
                break;

            var assembly = method.DeclaringType?.Assembly;
            if (assembly == null)
                continue;

            var mod = _modResolver(assembly);
            if (mod != null)
                return mod;
        }

        return null;
    }
}
