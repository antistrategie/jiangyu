using System.Text.Json.Serialization;
using InfiniFrame;

namespace Jiangyu.Studio.Host;

/// <summary>
/// Watches the currently open project root and pushes debounced
/// <c>fileChanged</c> notifications to the frontend. Callers can suppress
/// upcoming events for a path (e.g. during our own writeFile) so that
/// self-writes don't trigger the conflict banner.
/// </summary>
internal static class ProjectWatcher
{
    private const int DebounceMs = 100;
    private const int DefaultSuppressMs = 500;

    // Path segments that are always excluded regardless of .gitignore.
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", ".git", "Library", "Temp", ".vs", ".idea",
    };

    private static FileSystemWatcher? _watcher;
    private static IInfiniFrameWindow? _window;

    private static readonly Dictionary<string, long> SuppressUntil = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Timer> Pending = new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    public static void Start(IInfiniFrameWindow window, string root)
    {
        Stop();

        _window = window;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        // Raw event kind is unreliable: editors that save via write-temp +
        // delete-old + rename-new produce a Deleted event for the real path
        // even though the file still exists afterwards. We keyed-coalesce on
        // the path and recompute the real kind at flush time from disk state.
        _watcher.Changed += (_, e) => QueueEvent(e.FullPath);
        _watcher.Created += (_, e) => QueueEvent(e.FullPath);
        _watcher.Deleted += (_, e) => QueueEvent(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            QueueEvent(e.FullPath);
            QueueEvent(e.OldFullPath);
        };
        _watcher.Error += (_, e) =>
            Console.Error.WriteLine($"[Watcher] {e.GetException().Message}");
    }

    public static void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (Lock)
        {
            foreach (var timer in Pending.Values)
                timer.Dispose();
            Pending.Clear();
            SuppressUntil.Clear();
        }
    }

    /// <summary>Suppress upcoming events for <paramref name="path"/> for a short window.</summary>
    public static void SuppressFor(string path, int milliseconds = DefaultSuppressMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (Lock)
        {
            SuppressUntil[path] = now + milliseconds;

            // Normally entries are evicted on the matching FS event. If that
            // event never arrives (excluded path, FSW miss, …) the map grows
            // unboundedly, so opportunistically sweep when it gets large.
            if (SuppressUntil.Count > 16)
            {
                var expired = SuppressUntil
                    .Where(kv => kv.Value <= now)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in expired)
                    SuppressUntil.Remove(key);
            }
        }
    }

    private static bool IsExcluded(string fullPath)
    {
        // Scan path segments in place; avoids allocating a split array on
        // every FS event (this method runs on the watcher thread for
        // every raw event, including those in excluded trees).
        var separator = Path.DirectorySeparatorChar;
        var start = 0;
        for (var i = 0; i <= fullPath.Length; i++)
        {
            if (i != fullPath.Length && fullPath[i] != separator) continue;
            if (i > start)
            {
                var segment = fullPath.AsSpan(start, i - start);
                foreach (var ex in ExcludedSegments)
                {
                    if (segment.SequenceEqual(ex.AsSpan()))
                        return true;
                }
            }
            start = i + 1;
        }
        return false;
    }

    private static bool IsSuppressed(string path)
    {
        lock (Lock)
        {
            if (!SuppressUntil.TryGetValue(path, out var expiry)) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < expiry) return true;
            SuppressUntil.Remove(path);
            return false;
        }
    }

    private static void QueueEvent(string path)
    {
        if (IsExcluded(path)) return;

        lock (Lock)
        {
            if (Pending.TryGetValue(path, out var prev))
                prev.Dispose();

            Pending[path] = new Timer(_ => FlushEvent(path), null, DebounceMs, Timeout.Infinite);
        }
    }

    private static void FlushEvent(string path)
    {
        lock (Lock)
        {
            if (!Pending.Remove(path, out var timer)) return;
            timer.Dispose();
        }

        if (IsSuppressed(path)) return;

        var window = _window;
        if (window is null) return;

        var exists = File.Exists(path) || Directory.Exists(path);
        var kind = exists ? "changed" : "deleted";

        try
        {
            RpcDispatcher.SendNotification(window, "fileChanged", new FileChangedEvent
            {
                Path = path,
                Kind = kind,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Watcher] SendNotification failed: {ex.Message}");
        }
    }

    internal sealed class FileChangedEvent
    {
        [JsonPropertyName("path")]
        public required string Path { get; set; }

        [JsonPropertyName("kind")]
        public required string Kind { get; set; }
    }
}
