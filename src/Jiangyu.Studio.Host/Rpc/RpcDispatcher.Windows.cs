using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    // Spawns a secondary InfiniFrameWindow that loads the same SPA with
    // `?window=pane` appended, so the frontend renders its pane shell instead
    // of the full app. The secondary gets its own isolated web-message channel
    // and subscribes to ProjectWatcher so it receives fileChanged events
    // independently.
    //
    // On Linux, InfiniFrame wraps a single global gtk_main() loop and all
    // window construction must happen on the main (primary) thread. We
    // marshal the Build onto it via IInfiniFrameWindow.Invoke and never call
    // WaitForClose on the secondary — the primary's gtk_main pumps events for
    // both. Same pattern is safe on Windows (both windows share the primary
    // thread's message queue).
    private static readonly List<IInfiniFrameWindow> Secondaries = [];
    private static readonly Lock TabDragLock = new();
    private static (IInfiniFrameWindow Source, string Path, long BeganAtMs)? _tabDrag;
    private static readonly Lock PaneDragLock = new();
    private static (IInfiniFrameWindow Source, long BeganAtMs)? _paneDrag;

    // How long a cross-window drag's state is considered "in flight" on the
    // host. Beyond this, completeX calls ignore the record — a stale record
    // from a cancelled / timed-out drag can't accidentally close the wrong
    // window. Fresh beginX always overwrites.
    private const int DragStateTtlMs = 5000;

    // True while HandleCloseAllPaneWindows is driving the close loop. Secondary
    // close handlers check this to decide whether to emit paneWindowClosed —
    // a bulk close (project closed/switched) should preserve the localStorage
    // record so windows can be restored on reopen.
    private static bool _silentClose;

    // Captured on first openPaneWindow call so the secondary's close handler
    // can push paneWindowClosed to the primary, even when fired from the
    // secondary's own InfiniFrame event pump.
    private static IInfiniFrameWindow? _primary;

    private static JsonElement HandleOpenPaneWindow(IInfiniFrameWindow primary, JsonElement? parameters)
    {
        var startUrl = primary.StartUrl
            ?? throw new InvalidOperationException("Primary window has no StartUrl.");

        var paneUrl = AppendQuery(startUrl, "window", "pane");
        var kind = TryGetString(parameters, "kind");
        if (kind is not null)
            paneUrl = AppendQuery(paneUrl, "kind", kind);
        var projectPath = TryGetString(parameters, "projectPath") ?? ProjectWatcher.ProjectRoot;
        if (projectPath is not null)
            paneUrl = AppendQuery(paneUrl, "projectPath", projectPath);

        // filePaths carries every tab in a code pane so the secondary can
        // rebuild the tab strip. Single-file callers can still pass filePath
        // for convenience; it's folded into the array.
        foreach (var path in GetStringArray(parameters, "filePaths"))
            paneUrl = AppendQuery(paneUrl, "filePath", path);
        var singleFilePath = TryGetString(parameters, "filePath");
        if (singleFilePath is not null)
            paneUrl = AppendQuery(paneUrl, "filePath", singleFilePath);
        var activeFilePath = TryGetString(parameters, "activeFilePath");
        if (activeFilePath is not null)
            paneUrl = AppendQuery(paneUrl, "activeFilePath", activeFilePath);

        // Browser state (query/filter/selection/…) rides along as a JSON blob
        // so secondaries can seed AssetBrowser / TemplateBrowser with the
        // tear-out-time state or last-persisted state.
        if (parameters is { } p && p.TryGetProperty("browserState", out var browserStateProp)
            && browserStateProp.ValueKind == JsonValueKind.Object)
        {
            paneUrl = AppendQuery(paneUrl, "browserState", browserStateProp.GetRawText());
        }

        var title = TitleFor(kind, activeFilePath);

        _primary ??= primary;

        string? createdWindowId = null;
        primary.Invoke(() =>
        {
            var secondary = InfiniFrameWindowBuilder.Create()
                .SetTitle(title)
                .SetSize(new Size(1200, 800))
                .SetStartUrl(paneUrl)
                .Center()
                .RegisterWebMessagePostHandler(RpcMessageId,
                    (window, payload) =>
                        HandleMessage(window, payload ?? string.Empty, window.SendWebMessage))
                .Build();

            lock (Secondaries) Secondaries.Add(secondary);
            ProjectWatcher.Subscribe(secondary);
            createdWindowId = secondary.Id.ToString();

            // Closing handler returns WindowClosingResult.Close to proceed.
            // Unsubscribe from the watcher so notifications stop fanning out
            // to a dying window and release our strong reference so GC can
            // reclaim it. User-initiated closes also emit paneWindowClosed so
            // the primary can drop the descriptor from its restore list;
            // bulk project closes suppress that so the list is preserved.
            secondary.EventsStore.Closing.Add((w, _) =>
            {
                ProjectWatcher.Unsubscribe(w);
                lock (Secondaries) Secondaries.Remove(w);
                if (!_silentClose && _primary is { } primaryWindow)
                {
                    try
                    {
                        SendNotification(primaryWindow, "paneWindowClosed",
                            new { windowId = w.Id.ToString() });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[openPaneWindow] close emit failed: {ex.Message}");
                    }
                }
                return WindowClosingResult.Close;
            });
        });

        return JsonSerializer.SerializeToElement(new PaneWindowOpenedAck
        {
            Opened = true,
            WindowId = createdWindowId ?? string.Empty,
        });
    }

    // Cross-window tab drag: source registers intent on dragstart; a target
    // that consumes the drop fires completeTabMove, and the host emits
    // tabMovedOut to the source so it can drop the tab. There's no cancel —
    // the record expires via DragStateTtlMs. A source-side cancel raced the
    // target's complete when run explicitly (dragend fires synchronously but
    // RPCs arrive in any order), silently losing the close-at-source signal.
    private static JsonElement HandleBeginTabMove(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        lock (TabDragLock)
        {
            _tabDrag = (window, path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        return NullElement;
    }

    private static JsonElement HandleCompleteTabMove(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var path = RequireString(parameters, "path");
        (IInfiniFrameWindow Source, string Path, long BeganAtMs)? state;
        lock (TabDragLock)
        {
            state = _tabDrag;
            _tabDrag = null;
        }
        if (state is null) return NullElement;
        if (state.Value.Source == window) return NullElement; // dropped in own window
        if (!string.Equals(state.Value.Path, path, StringComparison.Ordinal)) return NullElement;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Value.BeganAtMs > DragStateTtlMs)
            return NullElement; // stale (came from a cancelled drag)
        try
        {
            SendNotification(state.Value.Source, "tabMovedOut", new { path });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tabMove] notify source failed: {ex.Message}");
        }
        return NullElement;
    }

    // Cross-window PANE drag: a secondary is being dragged back into the
    // primary as a whole pane (not a single tab). Payload travels via HTML5
    // dataTransfer; host only tracks the source so it can notify it to close
    // itself when the drop is consumed. Same TTL pattern as tab drag — no
    // cancel RPC, so source-side cleanup can't race the target's complete.
    private static JsonElement HandleBeginPaneMove(IInfiniFrameWindow window, JsonElement? _)
    {
        lock (PaneDragLock)
        {
            _paneDrag = (window, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        return NullElement;
    }

    private static JsonElement HandleCompletePaneMove(IInfiniFrameWindow window, JsonElement? _)
    {
        (IInfiniFrameWindow Source, long BeganAtMs)? state;
        lock (PaneDragLock)
        {
            state = _paneDrag;
            _paneDrag = null;
        }
        if (state is null) return NullElement;
        if (state.Value.Source == window) return NullElement; // dropped in own window
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Value.BeganAtMs > DragStateTtlMs)
            return NullElement; // stale
        try
        {
            SendNotification(state.Value.Source, "paneMovedOut", new { });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[paneMove] notify source failed: {ex.Message}");
        }
        return NullElement;
    }

    // Mirror a secondary's current browser state (query/filter/selection/…)
    // up to the primary so its descriptor persists it. The payload's state
    // object is opaque JSON — primary decides how to store.
    private static JsonElement HandleUpdatePaneWindowBrowserState(
        IInfiniFrameWindow window,
        JsonElement? parameters)
    {
        if (_primary is not { } primary || window == primary) return NullElement;
        if (parameters is not { } p || !p.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.Object)
            return NullElement;
        try
        {
            SendNotification(primary, "paneWindowBrowserStateChanged", new
            {
                windowId = window.Id.ToString(),
                state = JsonSerializer.Deserialize<JsonElement>(state.GetRawText()),
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[updatePaneWindowBrowserState] notify failed: {ex.Message}");
        }
        return NullElement;
    }

    // Mirror the secondary's current tab state up to the primary so its
    // localStorage descriptor reflects drag-ins / drag-outs / closes, not
    // just whatever was passed to openPaneWindow. Primary listens for
    // paneWindowTabsChanged and refreshes storage.
    private static JsonElement HandleUpdatePaneWindowTabs(
        IInfiniFrameWindow window,
        JsonElement? parameters)
    {
        var filePaths = GetStringArray(parameters, "filePaths").ToArray();
        var activeFilePath = TryGetString(parameters, "activeFilePath");
        if (_primary is not { } primary || window == primary) return NullElement;

        try
        {
            SendNotification(primary, "paneWindowTabsChanged", new
            {
                windowId = window.Id.ToString(),
                filePaths,
                activeFilePath,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[updatePaneWindowTabs] notify failed: {ex.Message}");
        }
        return NullElement;
    }

    // document.title doesn't reliably bubble to the native OS title on
    // WebKitGTK, so let the UI push titles explicitly. The call routes to
    // the caller's own window — there's no cross-window title spoofing.
    private static JsonElement HandleSetWindowTitle(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var title = RequireString(parameters, "title");
        window.Invoke(() => window.SetTitle(title));
        return NullElement;
    }

    // Close the calling window. Used by pane windows whose last tab was
    // closed — they close the OS window rather than sitting empty.
    private static JsonElement HandleCloseSelf(IInfiniFrameWindow window, JsonElement? _)
    {
        // Must marshal to the window's own thread — Close is native.
        window.Invoke(() =>
        {
            try { window.Close(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[closeSelf] failed: {ex.Message}");
            }
        });
        return NullElement;
    }

    // Closes every secondary pane window. Called when the UI closes or switches
    // project so dangling windows don't sit on a now-unwatched root. Close is
    // marshalled onto the primary's thread because InfiniFrame's native window
    // ops require main-thread affinity on Linux/Mac.
    private static JsonElement HandleCloseAllPaneWindows(IInfiniFrameWindow primary, JsonElement? _)
    {
        IInfiniFrameWindow[] snapshot;
        lock (Secondaries) snapshot = [.. Secondaries];
        primary.Invoke(() =>
        {
            _silentClose = true;
            try
            {
                foreach (var window in snapshot)
                {
                    try { window.Close(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[openPaneWindow] close failed: {ex.Message}");
                    }
                }
            }
            finally { _silentClose = false; }
        });
        return NullElement;
    }

    private static string TitleFor(string? kind, string? activeFilePath)
    {
        return kind switch
        {
            "assetBrowser" => "Jiangyu Studio — Asset Browser",
            "templateBrowser" => "Jiangyu Studio — Template Browser",
            "code" when !string.IsNullOrEmpty(activeFilePath) =>
                $"Jiangyu Studio — {Path.GetFileName(activeFilePath)}",
            "code" => "Jiangyu Studio — Code",
            _ => "Jiangyu Studio — Pane",
        };
    }

    private static string AppendQuery(string url, string key, string value)
    {
        var builder = new UriBuilder(url);
        var existing = builder.Query.TrimStart('?');
        var encoded = $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        builder.Query = string.IsNullOrEmpty(existing) ? encoded : $"{existing}&{encoded}";
        return builder.Uri.ToString();
    }

    private static IEnumerable<string> GetStringArray(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop)) yield break;
        if (prop.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } str)
                yield return str;
        }
    }

    [RpcType]
    internal sealed class PaneWindowOpenedAck
    {
        [JsonPropertyName("opened")]
        public required bool Opened { get; set; }

        [JsonPropertyName("windowId")]
        public required string WindowId { get; set; }
    }
}
