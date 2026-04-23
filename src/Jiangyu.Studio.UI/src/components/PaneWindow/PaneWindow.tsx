import { useCallback, useEffect, useRef, useState } from "react";
import { AssetBrowser } from "../AssetBrowser/AssetBrowser.tsx";
import { TemplateBrowser } from "../TemplateBrowser/TemplateBrowser.tsx";
import { TabbedMonacoEditor } from "../CodeEditor/TabbedMonacoEditor.tsx";
import { rpcCall, subscribe, type FileChangeKind, type FileChangedEvent } from "../../lib/rpc.ts";
import {
  CROSS_TAB_MIME,
  beginTabMove,
  completeTabMove,
  encodeCrossTabPayload,
  parseCrossTabPayload,
  subscribeTabMovedOut,
} from "../../lib/crossWindowTabDrag.ts";
import {
  CROSS_PANE_MIME,
  beginPaneMove,
  encodeCrossPanePayload,
  subscribePaneMovedOut,
} from "../../lib/crossWindowPaneDrag.ts";
import type { PaneKind, Tab } from "../../lib/layout.ts";
import type { AssetBrowserState, TemplateBrowserState } from "../../lib/browserState.ts";
import { basename } from "../../lib/path.ts";
import editorStyles from "../EditorArea/EditorArea.module.css";
import styles from "./PaneWindow.module.css";

interface PaneWindowProps {
  readonly paneKind: PaneKind | null;
  readonly projectPath: string | null;
  readonly filePaths: readonly string[];
  readonly activeFilePath: string | null;
  readonly browserState: AssetBrowserState | TemplateBrowserState | null;
}

// Secondary-window shell. Dispatches on `paneKind` so a tear-out matches the
// originating pane's type: code → tabbed Monaco editor, assetBrowser →
// AssetBrowser, templateBrowser → TemplateBrowser. Writes originating here
// route through the same host filesystem handlers, and the watcher fans
// change notifications out to every window so edits converge via the
// conflict banner rather than a bespoke sync channel.
export function PaneWindow({
  paneKind,
  projectPath,
  filePaths,
  activeFilePath,
  browserState,
}: PaneWindowProps) {
  useEffect(() => {
    document.title = titleFor(paneKind, activeFilePath);
  }, [paneKind, activeFilePath]);

  // Source window's signal that the whole pane has been re-homed in another
  // window — close ourselves. Fires for any kind; the host only sends it
  // to the window that called beginPaneMove.
  useEffect(() => {
    return subscribePaneMovedOut(() => {
      void rpcCall<null>("closeSelf").catch(() => {});
    });
  }, []);

  if (paneKind === "assetBrowser") {
    if (projectPath === null) return <Placeholder text="No project path" />;
    return (
      <AssetBrowserShell
        projectPath={projectPath}
        initialState={browserState as AssetBrowserState | null}
      />
    );
  }

  if (paneKind === "templateBrowser") {
    if (projectPath === null) return <Placeholder text="No project path" />;
    return (
      <TemplateBrowserShell
        projectPath={projectPath}
        initialState={browserState as TemplateBrowserState | null}
      />
    );
  }

  if (filePaths.length === 0) return <Placeholder text="Pane window · 副窗口" />;
  return <CodePaneShell filePaths={filePaths} initialActive={activeFilePath} />;
}

function titleFor(kind: PaneKind | null, activeFilePath: string | null): string {
  if (kind === "assetBrowser") return "Jiangyu Studio — Asset Browser";
  if (kind === "templateBrowser") return "Jiangyu Studio — Template Browser";
  if (kind === "code" && activeFilePath !== null) {
    return `Jiangyu Studio — ${basename(activeFilePath)}`;
  }
  if (kind === "code") return "Jiangyu Studio — Code";
  return "Jiangyu Studio — Pane";
}

function Placeholder({ text }: { text: string }) {
  return (
    <div className={styles.shell}>
      <div className={styles.placeholder}>{text}</div>
    </div>
  );
}

// Code pane adapter: owns its own contents / dirty / conflict state and
// renders the shared <TabbedMonacoEditor>. Save + reload route directly to
// writeFile / readFile; the watcher's fileChanged surfaces conflict banners
// the same way the primary does.
function CodePaneShell({
  filePaths,
  initialActive,
}: {
  readonly filePaths: readonly string[];
  readonly initialActive: string | null;
}) {
  const [tabs, setTabs] = useState<Tab[]>(() =>
    filePaths.map((path) => ({ path, name: basename(path) })),
  );
  const [activePath, setActivePath] = useState<string | null>(() => {
    if (initialActive !== null && filePaths.includes(initialActive)) return initialActive;
    return filePaths[0] ?? null;
  });
  const [contents, setContents] = useState<Map<string, string>>(new Map());
  const [dirty, setDirty] = useState<Set<string>>(new Set());
  const [conflicts, setConflicts] = useState<Map<string, FileChangeKind>>(new Map());
  const [tabbarDropHover, setTabbarDropHover] = useState(false);
  // Path of the tab currently being dragged out; cleared on dragend or
  // when tabMovedOut fires for this path.
  const draggingRef = useRef<string | null>(null);

  // Push title updates through the host as well — WebKitGTK doesn't always
  // forward document.title to the native window title bar.
  useEffect(() => {
    if (activePath === null) return;
    const title = `Jiangyu Studio — ${basename(activePath)}`;
    document.title = title;
    void rpcCall<null>("setWindowTitle", { title }).catch(() => {});
  }, [activePath]);

  // Lazy-load the active tab's content on demand.
  useEffect(() => {
    if (activePath === null) return;
    if (contents.has(activePath)) return;
    let cancelled = false;
    rpcCall<string>("readFile", { path: activePath }).then(
      (text) => {
        if (cancelled) return;
        setContents((prev) => {
          if (prev.has(activePath)) return prev;
          const next = new Map(prev);
          next.set(activePath, text);
          return next;
        });
      },
      () => {
        /* swallow — conflict banner will surface the issue */
      },
    );
    return () => {
      cancelled = true;
    };
  }, [activePath, contents]);

  // Subscribe to watcher events for every tracked path. Clean tabs reload
  // silently; dirty / deleted paths raise the banner.
  useEffect(() => {
    const tracked = new Set(tabs.map((t) => t.path));
    return subscribe<FileChangedEvent>("fileChanged", (evt) => {
      if (!tracked.has(evt.path)) return;
      if (evt.kind === "deleted") {
        setConflicts((prev) => {
          const next = new Map(prev);
          next.set(evt.path, "deleted");
          return next;
        });
        return;
      }
      setDirty((currentDirty) => {
        if (currentDirty.has(evt.path)) {
          setConflicts((prev) => {
            const next = new Map(prev);
            next.set(evt.path, "changed");
            return next;
          });
        } else {
          rpcCall<string>("readFile", { path: evt.path }).then(
            (text) => {
              setContents((prev) => {
                const next = new Map(prev);
                next.set(evt.path, text);
                return next;
              });
            },
            () => {},
          );
        }
        return currentDirty;
      });
    });
  }, [tabs]);

  const handleActiveContentChange = useCallback(
    (value: string) => {
      if (activePath === null) return;
      setContents((prev) => {
        const next = new Map(prev);
        next.set(activePath, value);
        return next;
      });
      setDirty((prev) => (prev.has(activePath) ? prev : new Set(prev).add(activePath)));
    },
    [activePath],
  );

  const handleSave = useCallback(async () => {
    if (activePath === null) return;
    const content = contents.get(activePath);
    if (content === undefined) return;
    await rpcCall<void>("writeFile", { path: activePath, content });
    setDirty((prev) => {
      if (!prev.has(activePath)) return prev;
      const next = new Set(prev);
      next.delete(activePath);
      return next;
    });
  }, [activePath, contents]);

  const handleReload = useCallback(async () => {
    if (activePath === null) return;
    const text = await rpcCall<string>("readFile", { path: activePath });
    setContents((prev) => {
      const next = new Map(prev);
      next.set(activePath, text);
      return next;
    });
    setDirty((prev) => {
      if (!prev.has(activePath)) return prev;
      const next = new Set(prev);
      next.delete(activePath);
      return next;
    });
    setConflicts((prev) => {
      if (!prev.has(activePath)) return prev;
      const next = new Map(prev);
      next.delete(activePath);
      return next;
    });
  }, [activePath]);

  const handleDismissConflict = useCallback(() => {
    if (activePath === null) return;
    setConflicts((prev) => {
      if (!prev.has(activePath)) return prev;
      const next = new Map(prev);
      next.delete(activePath);
      return next;
    });
  }, [activePath]);

  const handleCloseTab = useCallback(
    (path: string) => {
      setTabs((prev) => {
        const idx = prev.findIndex((t) => t.path === path);
        if (idx === -1) return prev;
        const next = prev.slice(0, idx).concat(prev.slice(idx + 1));
        if (path === activePath) {
          const neighbour = next[Math.max(0, idx - 1)] ?? next[0] ?? null;
          setActivePath(neighbour?.path ?? null);
        }
        return next;
      });
      setDirty((prev) => {
        if (!prev.has(path)) return prev;
        const next = new Set(prev);
        next.delete(path);
        return next;
      });
      setConflicts((prev) => {
        if (!prev.has(path)) return prev;
        const next = new Map(prev);
        next.delete(path);
        return next;
      });
      setContents((prev) => {
        if (!prev.has(path)) return prev;
        const next = new Map(prev);
        next.delete(path);
        return next;
      });
    },
    [activePath],
  );

  // Remove the tab from local state when the host reports our drag was
  // consumed by another window.
  useEffect(() => {
    return subscribeTabMovedOut((path) => {
      if (draggingRef.current !== path) return;
      draggingRef.current = null;
      handleCloseTab(path);
    });
  }, [handleCloseTab]);

  // Close the OS window when the last tab is gone. We call closeSelf on the
  // host because `window.close()` in JS is restricted unless the window was
  // opened via script — the host-side InfiniFrameWindow.Close is universal.
  useEffect(() => {
    if (tabs.length > 0) return;
    void rpcCall<null>("closeSelf").catch(() => {});
  }, [tabs]);

  // Keep the primary's stored descriptor for this window in sync with the
  // current tab state. Without this, dragging a tab in/out of the secondary
  // wouldn't be reflected after a project reopen.
  useEffect(() => {
    void rpcCall<null>("updatePaneWindowTabs", {
      filePaths: tabs.map((t) => t.path),
      activeFilePath: activePath,
    }).catch(() => {});
  }, [tabs, activePath]);

  const handleTabDragStart = useCallback((e: React.DragEvent<HTMLButtonElement>, path: string) => {
    e.dataTransfer.setData(CROSS_TAB_MIME, encodeCrossTabPayload(path));
    e.dataTransfer.effectAllowed = "move";
    draggingRef.current = path;
    void beginTabMove(path).catch(() => {});
  }, []);

  const handleTabbarDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer.types.includes(CROSS_TAB_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    setTabbarDropHover(true);
  }, []);

  const handleTabbarDragLeave = useCallback(() => setTabbarDropHover(false), []);

  const handleTabbarDrop = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    setTabbarDropHover(false);
    const path = parseCrossTabPayload(e.dataTransfer.getData(CROSS_TAB_MIME));
    if (path === null) return;
    e.preventDefault();
    // Drop from this same window: the tab's already present, no-op.
    if (draggingRef.current === path) return;
    setTabs((prev) => {
      if (prev.some((t) => t.path === path)) return prev;
      return [...prev, { path, name: basename(path) }];
    });
    setActivePath(path);
    void completeTabMove(path).catch(() => {});
  }, []);

  // Whole-pane drag: the empty area after the tabs (tabFill) drags the whole
  // pane back to the primary. Carries the tab list + active tab so primary
  // can reconstruct identically.
  const handlePaneDragStart = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      e.dataTransfer.setData(
        CROSS_PANE_MIME,
        encodeCrossPanePayload({
          kind: "code",
          filePaths: tabs.map((t) => t.path),
          activeFilePath: activePath,
        }),
      );
      e.dataTransfer.effectAllowed = "move";
      void beginPaneMove().catch(() => {});
    },
    [tabs, activePath],
  );

  const activeContent = activePath !== null ? contents.get(activePath) : undefined;
  const activeConflict = activePath !== null ? (conflicts.get(activePath) ?? null) : null;

  return (
    <div className={styles.shell}>
      <TabbedMonacoEditor
        tabs={tabs}
        activePath={activePath}
        activeContent={activeContent}
        activeConflict={activeConflict}
        dirtyFiles={dirty}
        onSelectTab={setActivePath}
        onCloseTab={handleCloseTab}
        onActiveContentChange={handleActiveContentChange}
        onSave={handleSave}
        onReload={handleReload}
        onDismissConflict={handleDismissConflict}
        tabDrag={{
          onTabDragStart: handleTabDragStart,
          onTabbarDragOver: handleTabbarDragOver,
          onTabbarDragLeave: handleTabbarDragLeave,
          onTabbarDrop: handleTabbarDrop,
          dropHover: tabbarDropHover,
        }}
        onTabbarDragStart={handlePaneDragStart}
        className={editorStyles.editorActive}
      />
    </div>
  );
}

// Shared state-sync + drag-payload scaffolding for a browser pane window.
// latestStateRef keeps the most recent snapshot around so the pane-drag
// handler picks it up by reference when the user drags the window back
// into the primary (the drag event fires with whatever was current, not
// what was last emitted to React state).
function useBrowserPaneShell<TState extends AssetBrowserState | TemplateBrowserState>(
  kind: "assetBrowser" | "templateBrowser",
  initialState: TState | null,
): {
  readonly onStateChange: (state: TState) => void;
  readonly getPayload: () => string;
} {
  const latestStateRef = useRef<TState | null>(initialState);
  const onStateChange = useCallback((state: TState) => {
    latestStateRef.current = state;
    void rpcCall<null>("updatePaneWindowBrowserState", { state }).catch(() => {});
  }, []);
  const getPayload = useCallback(
    () =>
      encodeCrossPanePayload({
        kind,
        browserState: latestStateRef.current ?? undefined,
      }),
    [kind],
  );
  return { onStateChange, getPayload };
}

// AssetBrowser secondary: the top drag bar carries the cross-window pane
// payload so the whole window can be dragged back to the primary with its
// current browser state intact.
function AssetBrowserShell({
  projectPath,
  initialState,
}: {
  projectPath: string;
  initialState: AssetBrowserState | null;
}) {
  const { onStateChange, getPayload } = useBrowserPaneShell("assetBrowser", initialState);
  return (
    <div className={styles.shell}>
      <BrowserDragBar label="Asset Browser" getPayload={getPayload} />
      <AssetBrowser
        projectPath={projectPath}
        initialState={initialState ?? undefined}
        onStateChange={onStateChange}
      />
    </div>
  );
}

// Thin drag strip for browser pane windows. Behaves like the primary's
// tabFill — dragstart seeds the payload and tells the host a pane drag
// is in flight; host state expires via TTL if the drop never lands.
function BrowserDragBar({ label, getPayload }: { label: string; getPayload: () => string }) {
  return (
    <div
      className={styles.browserDragBar}
      draggable
      onDragStart={(e) => {
        e.dataTransfer.setData(CROSS_PANE_MIME, getPayload());
        e.dataTransfer.effectAllowed = "move";
        void beginPaneMove().catch(() => {});
      }}
      title="Drag to move pane to another window"
    >
      {label}
    </div>
  );
}

// Thin adapter that supplies the TemplateBrowser's file-system callbacks
// in a secondary window where there's no editor grid. onOpenFile tears out
// a new pane window; onAppendToFile reads+writes directly (the resulting
// fileChanged notification reaches the primary and surfaces the conflict
// banner there if the file is dirty).
function TemplateBrowserShell({
  projectPath,
  initialState,
}: {
  projectPath: string;
  initialState: TemplateBrowserState | null;
}) {
  const [fileEntries, setFileEntries] = useState<readonly string[]>([]);

  const refresh = useCallback(() => {
    rpcCall<string[]>("listAllFiles", { path: projectPath }).then(
      (files) => setFileEntries(files),
      (err: unknown) => {
        console.error("[PaneWindow] listAllFiles failed:", err);
      },
    );
  }, [projectPath]);

  useEffect(() => {
    refresh();
    const unsubscribe = subscribe<FileChangedEvent>("fileChanged", () => refresh());
    return unsubscribe;
  }, [refresh]);

  const openFileInNewWindow = useCallback(
    (path: string) => {
      void rpcCall("openPaneWindow", { kind: "code", projectPath, filePath: path });
    },
    [projectPath],
  );

  const appendToFile = useCallback(async (path: string, snippet: string) => {
    const current = await rpcCall<string>("readFile", { path }).catch(() => "");
    const separator = current.length === 0 || current.endsWith("\n") ? "" : "\n";
    await rpcCall<void>("writeFile", { path, content: current + separator + snippet });
  }, []);

  const { onStateChange, getPayload } = useBrowserPaneShell("templateBrowser", initialState);

  return (
    <div className={styles.shell}>
      <BrowserDragBar label="Template Browser" getPayload={getPayload} />
      <TemplateBrowser
        projectPath={projectPath}
        fileEntries={fileEntries}
        lastCodePath={null}
        onOpenFile={openFileInNewWindow}
        onAppendToFile={appendToFile}
        onRefreshFiles={refresh}
        initialState={initialState ?? undefined}
        onStateChange={onStateChange}
      />
    </div>
  );
}
