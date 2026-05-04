using System.Text.Json.Serialization;
using InfiniFrame;

namespace Jiangyu.Studio.Host.Rpc;

/// <summary>
/// Watches the currently open project root and pushes debounced
/// <c>fileChanged</c> notifications to every subscribed window. Callers can
/// suppress upcoming events for a <c>(path, originWindowId)</c> pair so that
/// a window's own writeFile doesn't trigger its own conflict banner — but
/// other windows still receive the notification and can reconcile.
/// </summary>
internal static class ProjectWatcher
{
    private const int DebounceMs = 100;
    private const int DefaultSuppressMs = 500;

    // Path segments that are always excluded regardless of .gitignore.
    // Case-insensitive so Windows paths like `Node_Modules` or `.Git` still match.
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", "Library", "Temp", ".vs", ".idea", ".jiangyu", ".unity"
    };

    private static FileSystemWatcher? _watcher;
    private static readonly List<IInfiniFrameWindow> Subscribers = [];

    /// <summary>
    /// Absolute path of the currently open project root, or null if no project is open.
    /// Used by the RPC dispatcher to enforce that filesystem operations stay within
    /// the project sandbox. Forwards to <see cref="Jiangyu.Studio.Rpc.RpcContext.ProjectRoot"/>
    /// so handlers in the shared Studio.Rpc library see the same value.
    /// </summary>
    public static string? ProjectRoot
    {
        get => Jiangyu.Studio.Rpc.RpcContext.ProjectRoot;
        internal set => Jiangyu.Studio.Rpc.RpcContext.ProjectRoot = value;
    }

    private static readonly Dictionary<(string Path, Guid WindowId), long> SuppressUntil = [];
    private static readonly Dictionary<string, Timer> Pending = new(StringComparer.Ordinal);
    private static readonly Lock Lock = new();

    public static void Start(IInfiniFrameWindow window, string root)
    {
        Stop();

        lock (Lock) Subscribers.Add(window);
        ProjectRoot = Path.GetFullPath(root);
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

        ProjectRoot = null;

        lock (Lock)
        {
            Subscribers.Clear();
            foreach (var timer in Pending.Values)
                timer.Dispose();
            Pending.Clear();
            SuppressUntil.Clear();
        }
    }

    /// <summary>
    /// Add a secondary window to receive <c>fileChanged</c> notifications for the
    /// current project. No-op if the window is already subscribed.
    /// </summary>
    public static void Subscribe(IInfiniFrameWindow window)
    {
        lock (Lock)
        {
            if (!Subscribers.Contains(window))
                Subscribers.Add(window);
        }
    }

    /// <summary>Remove a window (e.g. when it closes) from the subscriber list.</summary>
    public static void Unsubscribe(IInfiniFrameWindow window)
    {
        lock (Lock) Subscribers.Remove(window);
    }

    /// <summary>
    /// Snapshot of currently subscribed windows. Used by background tasks
    /// (e.g. agent-triggered compile streaming) that need to broadcast
    /// notifications to every open window without holding the
    /// <see cref="Lock"/> for the duration of the send.
    /// </summary>
    public static IReadOnlyList<IInfiniFrameWindow> SubscribedWindows()
    {
        lock (Lock) return [.. Subscribers];
    }

    /// <summary>
    /// Suppress upcoming <c>fileChanged</c> events for <paramref name="path"/>
    /// to <paramref name="originWindowId"/> only. Other subscribed windows
    /// still receive the notification — that's the cross-window conflict
    /// surface: a save in window A looks like an external edit to window B.
    /// </summary>
    public static void SuppressFor(string path, Guid originWindowId, int milliseconds = DefaultSuppressMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (Lock)
        {
            SuppressUntil[(path, originWindowId)] = now + milliseconds;

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
                    if (segment.Equals(ex.AsSpan(), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            start = i + 1;
        }
        return false;
    }

    private static bool IsSuppressedFor(string path, Guid windowId)
    {
        lock (Lock)
        {
            var key = (path, windowId);
            if (!SuppressUntil.TryGetValue(key, out var expiry)) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < expiry) return true;
            SuppressUntil.Remove(key);
            return false;
        }
    }

    /// <summary>
    /// Test probe: returns true when an unexpired suppression for
    /// <paramref name="path"/> and <paramref name="windowId"/> exists.
    /// Mirrors <see cref="IsSuppressedFor"/> but doesn't evict expired
    /// entries (so tests can assert without racing the GC sweep).
    /// </summary>
    internal static bool HasSuppressionForTesting(string path, Guid windowId)
    {
        lock (Lock)
        {
            if (!SuppressUntil.TryGetValue((path, windowId), out var expiry)) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < expiry;
        }
    }

    /// <summary>
    /// Test probe: clears all suppression entries.  Tests that exercise the
    /// editFile / writeFile wrappers should reset between runs so a leftover
    /// entry from one case doesn't masquerade as a fresh one.
    /// </summary>
    internal static void ResetSuppressionForTesting()
    {
        lock (Lock) SuppressUntil.Clear();
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

        IInfiniFrameWindow[] windows;
        lock (Lock) windows = [.. Subscribers];
        if (windows.Length == 0) return;

        var exists = File.Exists(path) || Directory.Exists(path);
        var kind = exists ? "changed" : "deleted";
        var payload = new FileChangedEvent { Path = path.Replace(Path.DirectorySeparatorChar, '/'), Kind = kind };

        foreach (var window in windows)
        {
            if (IsSuppressedFor(path, window.Id)) continue;
            try
            {
                RpcDispatcher.SendNotification(window, "fileChanged", payload);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Watcher] SendNotification failed: {ex.Message}");
            }
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
