using System.Diagnostics;
using Jiangyu.Loader.Logging;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

// Debug-only startup counters. Nested scopes overlap, so their totals identify expensive
// paths rather than adding up to wall time. Asset names never become dictionary keys.
internal static class StartupTimings
{
    private sealed class Entry
    {
        public double Total;
        public double Longest;
        public int Calls;
        public string Slowest;
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Milestones = new(StringComparer.Ordinal);
    private static MelonLogger.Instance _log;
    private static long _started;
    private static bool _active;

    public static void Begin(MelonLogger.Instance log)
    {
        Entries.Clear();
        Milestones.Clear();
        _log = log;
        _started = Stopwatch.GetTimestamp();
        _active = LoaderDebug.Enabled;
    }

    public static Scope Measure(string stage, string detail = null)
        => _active ? new Scope(stage, detail) : default;

    public static void MarkOnce(string stage, bool memory = false, string detail = null)
    {
        if (!_active || !LoaderDebug.Enabled)
            return;
        var label = detail == null ? stage : $"{stage}: {detail}";
        if (!Milestones.Add(label))
            return;
        LoaderDebug.Write(_log, $"Startup +{SecondsSince(_started):F3}s: {label}.");
        if (memory)
            MemoryReport.Write(_log, label, describeMachine: false);
    }

    public static void Complete()
    {
        if (!_active)
            return;
        MarkOnce("first scene follow-up complete");
        _active = false;
        LoaderDebug.Write(_log, "Startup timings, slowest 16 paths (nested totals overlap):");
        foreach (var pair in Entries.OrderByDescending(pair => pair.Value.Total).Take(16))
        {
            var entry = pair.Value;
            var detail = string.IsNullOrEmpty(entry.Slowest) ? string.Empty : $" ({entry.Slowest})";
            LoaderDebug.Write(_log, $"  {pair.Key}: {entry.Total:F3}s across {entry.Calls} call(s), longest {entry.Longest:F3}s{detail}.");
        }
        Entries.Clear();
        Milestones.Clear();
    }

    private static double SecondsSince(long timestamp)
        => (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency;

    internal readonly struct Scope : IDisposable
    {
        private readonly string _stage;
        private readonly string _detail;
        private readonly long _started;

        internal Scope(string stage, string detail)
        {
            _stage = stage;
            _detail = detail;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_stage == null || !_active)
                return;
            var seconds = SecondsSince(_started);
            if (!Entries.TryGetValue(_stage, out var entry))
                Entries[_stage] = entry = new Entry();
            entry.Total += seconds;
            entry.Calls++;
            if (seconds > entry.Longest)
            {
                entry.Longest = seconds;
                entry.Slowest = _detail;
            }
            // A slow call is visible even if startup never reaches the final summary.
            if (seconds >= 1)
                LoaderDebug.Write(_log, $"Startup slow operation: {_stage}, {seconds:F3}s" +
                    (string.IsNullOrEmpty(_detail) ? "." : $" ({_detail})."));
        }
    }
}
