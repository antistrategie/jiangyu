using System.Diagnostics;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

/// <summary>
/// One play-log line of memory figures at the two points a machine short on memory
/// dies: once the loader has materialised every bundle asset, and once the first scene's
/// replacement poll schedule has run to its end. A crash report carries this
/// line, so the first one also names the machine's RAM and graphics memory. Every figure
/// is read defensively: a Unity counter the player build compiles out, or one that reads
/// zero, is left off the line rather than printed as a number.
/// </summary>
internal static class MemoryReport
{
    public static void Write(MelonLogger.Instance log, string stage, bool describeMachine)
    {
        if (log == null)
            return;

        var figures = new List<string>();
        // Each counter stands alone so one that reads zero (PrivateMemorySize64 does under
        // Proton) drops out without taking a valid neighbour with it.
        Add(figures, () => Figure("process ", Process.GetCurrentProcess().WorkingSet64, " resident"));
        Add(figures, () => Figure("", Process.GetCurrentProcess().PrivateMemorySize64, " committed"));
        Add(figures, () => Figure("Unity ", UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(), " allocated"));
        Add(figures, () => Figure("", UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(), " reserved"));
        Add(figures, () => Figure("textures ", (long)UnityEngine.Texture.currentTextureMemory));
        Add(figures, () => Figure("graphics driver ", UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()));
        Add(figures, () => Figure("il2cpp heap ", IL2CPP.il2cpp_gc_get_used_size()));

        var line = $"Memory {stage}: {string.Join("; ", figures)}.";
        if (describeMachine)
        {
            var machine = Read(() =>
                $" Machine: {UnityEngine.SystemInfo.systemMemorySize / 1024.0:0.#} GB RAM, "
                + $"{UnityEngine.SystemInfo.graphicsMemorySize / 1024.0:0.#} GB graphics memory, "
                + $"{UnityEngine.SystemInfo.graphicsDeviceName}.");
            line += machine ?? string.Empty;
        }

        log.Msg(line);
    }

    private static void Add(List<string> figures, Func<string> reader)
    {
        var figure = Read(reader);
        if (figure != null)
            figures.Add(figure);
    }

    private static string Read(Func<string> reader)
    {
        try
        {
            return reader();
        }
        catch
        {
            return null;
        }
    }

    private static string Figure(string label, long bytes, string suffix = "")
        => bytes > 0 ? $"{label}{Size(bytes)}{suffix}" : null;

    private static string Size(long bytes)
        => bytes >= 1L << 30
            ? $"{bytes / (double)(1L << 30):0.0} GB"
            : $"{bytes / (double)(1L << 20):0} MB";
}
