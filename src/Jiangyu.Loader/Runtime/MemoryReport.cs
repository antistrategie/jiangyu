using System.Diagnostics;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

/// <summary>
/// Memory figures at startup boundaries. The first report also names the machine's
/// RAM and graphics memory. Counters that throw or return zero are omitted. Unity's
/// texture counter covers the engine's texture accounting, not measured VRAM residency.
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
        using var process = Process.GetCurrentProcess();
        Add(figures, () => Figure("process ", process.WorkingSet64, " resident"));
        Add(figures, () => Figure("", process.PeakWorkingSet64, " peak resident"));
        Add(figures, () => Figure("", process.PrivateMemorySize64, " committed"));
        Add(figures, () => Figure("Unity ", UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(), " allocated"));
        Add(figures, () => Figure("", UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(), " reserved"));
        Add(figures, () => Figure("textures ", (long)UnityEngine.Texture.currentTextureMemory));
        Add(figures, () => Figure("graphics driver ", UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()));
        Add(figures, () => Figure("il2cpp heap ", IL2CPP.il2cpp_gc_get_used_size()));

        var line = $"Memory {stage}: {string.Join(", ", figures)}.";
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
