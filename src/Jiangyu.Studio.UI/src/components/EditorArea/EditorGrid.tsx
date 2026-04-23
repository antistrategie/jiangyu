import {
  Fragment,
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
} from "react";
import type { editor as monacoEditor } from "monaco-editor";
import { rpcCall, subscribe, type FileChangedEvent, type FileChangeKind } from "../../lib/rpc.ts";
import { fileTargetCommands } from "../../lib/fileCommands.ts";
import { PALETTE_SCOPE, useRegisterActions, type PaletteAction } from "../../lib/actions.tsx";
import {
  columnWeight,
  getActiveCodePane,
  paneWeight,
  type BrowserPane as BrowserPaneModel,
  type Layout,
  type SplitEdge,
} from "../../lib/layout.ts";
import { EditorPane } from "./EditorPane.tsx";
import { BrowserPane } from "./BrowserPane.tsx";
import { EmptyPrompt } from "./EmptyPrompt.tsx";
import { ResizeHandle } from "./ResizeHandle.tsx";
import type { DropZone } from "./DropOverlay.tsx";
import {
  parseCrossTabPayload,
  completeTabMove,
  CROSS_TAB_MIME,
} from "../../lib/crossWindowTabDrag.ts";
import {
  CROSS_PANE_MIME,
  completePaneMove,
  parseCrossPanePayload,
} from "../../lib/crossWindowPaneDrag.ts";
import type { AssetBrowserState, TemplateBrowserState } from "../../lib/browserState.ts";
import {
  PANE_DRAG_MIME,
  TAB_DRAG_MIME,
  type PaneDragPayload,
  type TabDragPayload,
} from "./tabDrag.ts";
import { isBinaryFile } from "./fileTypes.ts";
import { isUsefulMonacoAction } from "./monacoActionFilter.ts";
import styles from "./EditorArea.module.css";

type ConflictKind = FileChangeKind;

export interface EditorGridHandle {
  /** Append a text snippet to the given file's editor buffer (marks dirty). */
  appendToFile(path: string, snippet: string): Promise<void>;
}

interface EditorGridProps {
  projectPath: string;
  layout: Layout;
  fullscreenPaneId: string | null;
  dirtyFiles: Set<string>;
  fileEntries: readonly string[];
  lastCodePath: string | null;
  onSelectTab: (paneId: string, path: string) => void;
  onSetActivePane: (paneId: string) => void;
  onCloseTabsInPane: (paneId: string, paths: readonly string[]) => void;
  onClosePane: (paneId: string) => void;
  onMoveTab: (fromPaneId: string, toPaneId: string, path: string) => void;
  onMarkDirty: (path: string, isDirty: boolean) => void;
  onOpenBrowserPane: (kind: BrowserPaneModel["kind"]) => void;
  onOpenFile: (path: string) => void;
  onAppendToFile: (path: string, snippet: string) => Promise<void>;
  onRefreshFiles: () => void;
  onResizeColumns: (
    leftId: string,
    leftWeight: number,
    rightId: string,
    rightWeight: number,
  ) => void;
  onResizePanes: (topId: string, topWeight: number, bottomId: string, bottomWeight: number) => void;
  onSplitWithTab: (fromPaneId: string, toPaneId: string, path: string, edge: SplitEdge) => void;
  onSplitAtEdgeWithPath: (toPaneId: string, path: string, edge: SplitEdge) => void;
  onMovePaneToEdge: (paneId: string, targetPaneId: string, edge: SplitEdge) => void;
  onSwapPanes: (paneIdA: string, paneIdB: string) => void;
  onConvertPane: (paneId: string, kind: BrowserPaneModel["kind"]) => void;
  onSplitFromPane: (paneId: string, direction: "right" | "down") => void;
  onToggleFullscreen: (paneId: string) => void;
  onTearOutPane: (paneId: string) => void;
  onCrossDragStart: (ticket: { paneId: string; path: string }) => void;
  onOpenFileInPane: (paneId: string, path: string) => void;
  onBrowserStateChange: (paneId: string, state: AssetBrowserState | TemplateBrowserState) => void;
  onInsertCrossWindowPane: (
    toPaneId: string,
    payload: ReturnType<typeof parseCrossPanePayload> & object,
    edge: "left" | "right" | "top" | "bottom",
  ) => void;
}

export const EditorGrid = forwardRef<EditorGridHandle, EditorGridProps>(function EditorGrid(
  {
    projectPath,
    layout,
    fullscreenPaneId,
    dirtyFiles,
    fileEntries,
    lastCodePath,
    onSelectTab,
    onSetActivePane,
    onCloseTabsInPane,
    onClosePane,
    onMoveTab,
    onMarkDirty,
    onOpenBrowserPane,
    onOpenFile,
    onAppendToFile,
    onRefreshFiles,
    onResizeColumns,
    onResizePanes,
    onSplitWithTab,
    onSplitAtEdgeWithPath,
    onMovePaneToEdge,
    onSwapPanes,
    onConvertPane,
    onSplitFromPane,
    onToggleFullscreen,
    onTearOutPane,
    onCrossDragStart,
    onOpenFileInPane,
    onBrowserStateChange,
    onInsertCrossWindowPane,
  },
  ref,
) {
  const [contents, setContents] = useState<Map<string, string>>(new Map());
  const [conflicts, setConflicts] = useState<Map<string, ConflictKind>>(new Map());
  const [editors, setEditors] = useState<Map<string, monacoEditor.IStandaloneCodeEditor>>(
    new Map(),
  );
  const inflightRef = useRef<Set<string>>(new Set());
  const columnElsRef = useRef<Map<string, HTMLElement>>(new Map());
  const paneElsRef = useRef<Map<string, HTMLElement>>(new Map());

  const registerColumnEl = useCallback((id: string, el: HTMLElement | null) => {
    if (el === null) columnElsRef.current.delete(id);
    else columnElsRef.current.set(id, el);
  }, []);
  const registerPaneEl = useCallback((id: string, el: HTMLElement | null) => {
    if (el === null) paneElsRef.current.delete(id);
    else paneElsRef.current.set(id, el);
  }, []);

  // Drag state — true while either a tab or a pane is mid-drag. Drop overlays
  // need to be shown and accept events only during a drag, so they don't block
  // ordinary clicks or Monaco interaction the rest of the time.
  const [dragActive, setDragActive] = useState(false);
  useEffect(() => {
    if (!dragActive) return;
    const onEnd = () => setDragActive(false);
    document.addEventListener("dragend", onEnd);
    document.addEventListener("drop", onEnd);
    return () => {
      document.removeEventListener("dragend", onEnd);
      document.removeEventListener("drop", onEnd);
    };
  }, [dragActive]);

  // Activate the drop overlays when an external drag carrying a Jiangyu tab
  // payload enters the window (e.g. cross-window drag from a secondary, or
  // a sidebar file drop that bubbles up to document level). In-window drags
  // set dragActive directly via handleDragStart; this covers cross-origin
  // where no dragstart fires on this document.
  useEffect(() => {
    const onEnter = (e: DragEvent) => {
      const types = e.dataTransfer?.types;
      if (types === undefined) return;
      const arr = Array.from(types);
      if (arr.includes(CROSS_TAB_MIME) || arr.includes(CROSS_PANE_MIME)) {
        setDragActive(true);
      }
    };
    document.addEventListener("dragenter", onEnter);
    return () => document.removeEventListener("dragenter", onEnter);
  }, []);

  const handleDragStart = useCallback(() => setDragActive(true), []);

  const handlePaneDrop = useCallback(
    (toPaneId: string, zone: DropZone, e: React.DragEvent) => {
      setDragActive(false);
      const tabRaw = e.dataTransfer.getData(TAB_DRAG_MIME);
      if (tabRaw.length > 0) {
        try {
          const { fromPaneId, path } = JSON.parse(tabRaw) as TabDragPayload;
          if (zone === "centre") {
            onMoveTab(fromPaneId, toPaneId, path);
          } else {
            onSplitWithTab(fromPaneId, toPaneId, path, zone);
          }
        } catch {
          // ignore malformed payload
        }
        return;
      }
      const paneRaw = e.dataTransfer.getData(PANE_DRAG_MIME);
      if (paneRaw.length > 0) {
        try {
          const { paneId } = JSON.parse(paneRaw) as PaneDragPayload;
          if (paneId === toPaneId) return;
          if (zone === "centre") {
            onSwapPanes(paneId, toPaneId);
          } else {
            onMovePaneToEdge(paneId, toPaneId, zone);
          }
        } catch {
          // ignore malformed payload
        }
        return;
      }
      // Cross-window PANE drag: a secondary's whole pane landing here.
      // Centre would need a "merge into this pane" semantic that doesn't
      // map cleanly across mixed kinds, so we coerce it to "right" — the
      // user gets a new pane next to the target. Source window closes
      // itself on paneMovedOut.
      const crossPaneRaw = e.dataTransfer.getData(CROSS_PANE_MIME);
      if (crossPaneRaw.length > 0) {
        const payload = parseCrossPanePayload(crossPaneRaw);
        if (payload !== null) {
          const edge = zone === "centre" ? "right" : zone;
          onInsertCrossWindowPane(toPaneId, payload, edge);
          void completePaneMove().catch(() => {});
        }
        return;
      }
      // Cross-window tab drag or sidebar file drag: centre zone opens a tab
      // in the pane, edge zones split a new pane with the file. completeTabMove
      // is a no-op for sidebar drags (no matching beginTabMove state) and
      // triggers close-at-source for cross-window tab drags.
      const crossPath = parseCrossTabPayload(e.dataTransfer.getData(CROSS_TAB_MIME));
      if (crossPath !== null) {
        if (zone === "centre") {
          onOpenFileInPane(toPaneId, crossPath);
        } else {
          onSplitAtEdgeWithPath(toPaneId, crossPath, zone);
        }
        void completeTabMove(crossPath).catch(() => {});
      }
    },
    [
      onMoveTab,
      onSplitWithTab,
      onMovePaneToEdge,
      onSwapPanes,
      onOpenFileInPane,
      onSplitAtEdgeWithPath,
      onInsertCrossWindowPane,
    ],
  );

  const activeCodePane = getActiveCodePane(layout);
  const activeFile = activeCodePane?.activeTab ?? null;

  const setContent = useCallback((path: string, text: string) => {
    setContents((prev) => {
      if (prev.get(path) === text) return prev;
      const next = new Map(prev);
      next.set(path, text);
      return next;
    });
  }, []);

  const setConflict = useCallback((path: string, kind: ConflictKind) => {
    setConflicts((prev) => {
      if (prev.get(path) === kind) return prev;
      const next = new Map(prev);
      next.set(path, kind);
      return next;
    });
  }, []);

  const clearConflict = useCallback((path: string) => {
    setConflicts((prev) => {
      if (!prev.has(path)) return prev;
      const next = new Map(prev);
      next.delete(path);
      return next;
    });
  }, []);

  // Refs so save/reload callbacks stay stable across content updates.
  const contentsRef = useRef(contents);
  const dirtyFilesRef = useRef(dirtyFiles);
  const onMarkDirtyRef = useRef(onMarkDirty);
  useEffect(() => {
    contentsRef.current = contents;
  }, [contents]);
  useEffect(() => {
    dirtyFilesRef.current = dirtyFiles;
  }, [dirtyFiles]);
  useEffect(() => {
    onMarkDirtyRef.current = onMarkDirty;
  }, [onMarkDirty]);

  useImperativeHandle(
    ref,
    () => ({
      async appendToFile(path: string, snippet: string) {
        // Coordinate with the activePaths loader so its background readFile
        // can't overwrite the appended snippet. Three cases:
        //  1) content already loaded — append directly.
        //  2) load in flight elsewhere — wait for it to populate contentsRef,
        //     then append.
        //  3) not loaded and not in flight — take ownership of the load here,
        //     marking inflight so the activePaths effect skips it; combine the
        //     loaded text with the snippet in a single setContent so there's
        //     no window where the loaded text sits without the snippet.
        if (!contentsRef.current.has(path) && !inflightRef.current.has(path)) {
          inflightRef.current.add(path);
          try {
            let text = "";
            try {
              text = await rpcCall<string>("readFile", { path });
            } catch {
              // File doesn't exist yet — treat as empty.
            }
            const sep = text.length === 0 ? "" : text.endsWith("\n") ? "\n" : "\n\n";
            setContent(path, text + sep + snippet);
            onMarkDirtyRef.current(path, true);
          } finally {
            inflightRef.current.delete(path);
          }
          return;
        }

        if (!contentsRef.current.has(path)) {
          // Loader in flight elsewhere — poll for it to land.
          const deadline = Date.now() + 5000;
          while (
            inflightRef.current.has(path) &&
            !contentsRef.current.has(path) &&
            Date.now() < deadline
          ) {
            await new Promise((r) => setTimeout(r, 20));
          }
        }

        const current = contentsRef.current.get(path) ?? "";
        const separator = current.length === 0 ? "" : current.endsWith("\n") ? "\n" : "\n\n";
        setContent(path, current + separator + snippet);
        onMarkDirtyRef.current(path, true);
      },
    }),
    [setContent],
  );

  const handleSave = useCallback(
    async (path: string) => {
      const content = contentsRef.current.get(path);
      if (content === undefined) return;
      try {
        await rpcCall<null>("writeFile", { path, content });
        onMarkDirtyRef.current(path, false);
        clearConflict(path);
      } catch (err) {
        console.error("[Editor] writeFile failed:", err);
      }
    },
    [clearConflict],
  );

  const handleReload = useCallback(
    async (path: string) => {
      try {
        const text = await rpcCall<string>("readFile", { path });
        setContent(path, text);
        onMarkDirtyRef.current(path, false);
        clearConflict(path);
      } catch (err) {
        console.error("[Editor] reload failed:", err);
      }
    },
    [clearConflict, setContent],
  );

  const handleEditorMount = useCallback(
    (paneId: string, editor: monacoEditor.IStandaloneCodeEditor) => {
      setEditors((prev) => {
        if (prev.get(paneId) === editor) return prev;
        const next = new Map(prev);
        next.set(paneId, editor);
        return next;
      });
      editor.onDidDispose(() => {
        setEditors((prev) => {
          if (prev.get(paneId) !== editor) return prev;
          const next = new Map(prev);
          next.delete(paneId);
          return next;
        });
      });
    },
    [],
  );

  const handleChange = useCallback(
    (path: string, value: string) => {
      setContent(path, value);
      onMarkDirtyRef.current(path, true);
    },
    [setContent],
  );

  // Monaco's automaticLayout ResizeObserver misses some sibling-column removals
  // (probably because flex redistribution happens in the same frame as the
  // DOM mutation). Force each live editor to re-measure after any layout
  // topology / weight change so the editor fills the grown pane.
  useEffect(() => {
    const id = requestAnimationFrame(() => {
      for (const editor of editors.values()) editor.layout();
    });
    return () => cancelAnimationFrame(id);
  }, [layout, editors, fullscreenPaneId]);

  // Single walk over the layout produces every derived set the grid needs:
  // currently-active file paths (to trigger lazy reads), all open file paths
  // (to prune stale content/conflict entries), and all pane ids (to prune the
  // editor registry).
  const { activePaths, openPaths, openPaneIds } = useMemo(() => {
    const active = new Set<string>();
    const open = new Set<string>();
    const paneIds = new Set<string>();
    for (const col of layout.columns) {
      for (const pane of col.panes) {
        paneIds.add(pane.id);
        if (pane.kind !== "code") continue;
        if (pane.activeTab !== null) active.add(pane.activeTab);
        for (const tab of pane.tabs) open.add(tab.path);
      }
    }
    return { activePaths: active, openPaths: open, openPaneIds: paneIds };
  }, [layout]);

  useEffect(() => {
    for (const path of activePaths) {
      if (contentsRef.current.has(path)) continue;
      if (isBinaryFile(path) || inflightRef.current.has(path)) continue;
      inflightRef.current.add(path);
      void rpcCall<string>("readFile", { path })
        .then((text) => {
          setContent(path, text);
        })
        .catch((err) => {
          console.error("[Editor] readFile failed:", err);
        })
        .finally(() => {
          inflightRef.current.delete(path);
        });
    }
  }, [activePaths, setContent]);

  useEffect(() => {
    const prune = <V,>(prev: Map<string, V>): Map<string, V> => {
      let changed = false;
      const next = new Map(prev);
      for (const k of next.keys()) {
        if (!openPaths.has(k)) {
          next.delete(k);
          changed = true;
        }
      }
      return changed ? next : prev;
    };
    setContents(prune);
    setConflicts(prune);
  }, [openPaths]);

  useEffect(() => {
    setEditors((prev) => {
      let changed = false;
      const next = new Map(prev);
      for (const k of next.keys()) {
        if (!openPaneIds.has(k)) {
          next.delete(k);
          changed = true;
        }
      }
      return changed ? next : prev;
    });
  }, [openPaneIds]);

  useEffect(() => {
    return subscribe<FileChangedEvent>("fileChanged", (event) => {
      if (!openPaths.has(event.path)) return;

      if (event.kind === "deleted") {
        setConflict(event.path, "deleted");
        return;
      }

      if (dirtyFilesRef.current.has(event.path)) {
        setConflict(event.path, "changed");
        return;
      }

      void rpcCall<string>("readFile", { path: event.path })
        .then((text) => {
          setContent(event.path, text);
        })
        .catch((err) => {
          console.error("[Editor] silent reload failed:", err);
        });
    });
  }, [openPaths, setConflict, setContent]);

  // Active-pane palette actions: file ops + Monaco actions of the focused editor.
  const activeEditor = activeCodePane !== null ? (editors.get(activeCodePane.id) ?? null) : null;

  const fileActions = useMemo<PaletteAction[]>(() => {
    if (activeCodePane === null || activeFile === null) return [];
    const paneId = activeCodePane.id;
    const commands = fileTargetCommands(activeFile, projectPath, (paths) =>
      onCloseTabsInPane(paneId, paths),
    );
    const close = commands.find((c) => c.id === "close")!;
    const extras = commands.filter((c) => c.id !== "close");

    const result: PaletteAction[] = [
      {
        id: "editor.save",
        label: "Save",
        scope: PALETTE_SCOPE.File,
        cn: "保存",
        kbd: "Ctrl+S",
        run: () => void handleSave(activeFile),
      },
      {
        id: "editor.close",
        label: "Close Tab",
        scope: PALETTE_SCOPE.File,
        ...(close.cn !== undefined ? { cn: close.cn } : {}),
        ...(close.shortcut !== undefined ? { kbd: close.shortcut } : {}),
        run: close.run,
      },
    ];
    for (const c of extras) {
      result.push({
        id: `editor.${c.id}`,
        label: c.label,
        scope: PALETTE_SCOPE.File,
        ...(c.cn !== undefined ? { cn: c.cn } : {}),
        ...(c.shortcut !== undefined ? { kbd: c.shortcut } : {}),
        ...(c.desc !== undefined ? { desc: c.desc } : {}),
        run: c.run,
      });
    }
    return result;
  }, [activeCodePane, activeFile, projectPath, onCloseTabsInPane, handleSave]);

  const monacoActions = useMemo<PaletteAction[]>(() => {
    if (!activeEditor) return [];
    // Monaco doesn't expose a public way to read an action's keybinding; the
    // standalone editor keeps it on the internal _standaloneKeybindingService.
    // Accessing it lets us show shortcuts next to each command like VS Code.
    const kbService = (
      activeEditor as unknown as {
        _standaloneKeybindingService?: {
          lookupKeybinding: (id: string) => { getLabel: () => string | null } | undefined;
        };
      }
    )._standaloneKeybindingService;

    return activeEditor
      .getSupportedActions()
      .filter((action) => isUsefulMonacoAction(action.id, action.label))
      .map((action) => {
        const kbd = kbService?.lookupKeybinding(action.id)?.getLabel() ?? undefined;
        return {
          id: `monaco.${action.id}`,
          label: action.label,
          scope: PALETTE_SCOPE.Editor,
          ...(kbd !== undefined && kbd.length > 0 ? { kbd } : {}),
          run: () => {
            activeEditor.focus();
            void action.run();
          },
        };
      });
  }, [activeEditor]);

  useRegisterActions(fileActions);
  useRegisterActions(monacoActions);

  if (layout.columns.length === 0) {
    return (
      <div className={styles.editor}>
        <div className={styles.content}>
          <EmptyPrompt onOpenBrowser={onOpenBrowserPane} />
        </div>
      </div>
    );
  }

  return (
    <div className={styles.grid}>
      {layout.columns.map((col, ci) => {
        const colEl = (
          <div
            key={col.id}
            ref={(el) => registerColumnEl(col.id, el)}
            className={styles.column}
            style={{ flex: columnWeight(col) }}
          >
            {col.panes.map((pane, pi) => {
              const isActive = pane.id === layout.activePaneId;
              const isFullscreen = fullscreenPaneId === pane.id;
              const paneNode =
                pane.kind === "code" ? (
                  <EditorPane
                    pane={pane}
                    isActive={isActive}
                    isFullscreen={isFullscreen}
                    flex={paneWeight(pane)}
                    registerEl={registerPaneEl}
                    dragActive={dragActive}
                    onTabDragStart={handleDragStart}
                    onPaneDragStart={handleDragStart}
                    onPaneDrop={handlePaneDrop}
                    onConvertPane={onConvertPane}
                    onSplitFromPane={onSplitFromPane}
                    onClosePane={onClosePane}
                    onToggleFullscreen={onToggleFullscreen}
                    onTearOutPane={onTearOutPane}
                    projectPath={projectPath}
                    dirtyFiles={dirtyFiles}
                    contents={contents}
                    conflicts={conflicts}
                    onSelectTab={onSelectTab}
                    onSetActive={onSetActivePane}
                    onCloseTabs={onCloseTabsInPane}
                    onMoveTab={onMoveTab}
                    onContentChange={handleChange}
                    onEditorMount={handleEditorMount}
                    onSave={handleSave}
                    onReload={handleReload}
                    onDismissConflict={clearConflict}
                    onCrossDragStart={onCrossDragStart}
                    onOpenFileInPane={onOpenFileInPane}
                  />
                ) : (
                  <BrowserPane
                    pane={pane}
                    projectPath={projectPath}
                    isActive={isActive}
                    isFullscreen={isFullscreen}
                    flex={paneWeight(pane)}
                    registerEl={registerPaneEl}
                    dragActive={dragActive}
                    fileEntries={fileEntries}
                    lastCodePath={lastCodePath}
                    onSetActive={onSetActivePane}
                    onClosePane={onClosePane}
                    onPaneDragStart={handleDragStart}
                    onPaneDrop={handlePaneDrop}
                    onSplitFromPane={onSplitFromPane}
                    onToggleFullscreen={onToggleFullscreen}
                    onTearOutPane={onTearOutPane}
                    onOpenFile={onOpenFile}
                    onAppendToFile={onAppendToFile}
                    onRefreshFiles={onRefreshFiles}
                    onBrowserStateChange={onBrowserStateChange}
                  />
                );
              if (pi === 0) return <Fragment key={pane.id}>{paneNode}</Fragment>;
              const prev = col.panes[pi - 1]!;
              return (
                <Fragment key={pane.id}>
                  <ResizeHandle
                    axis="y"
                    onResize={(deltaPx, start) => {
                      const total = start.aPx + start.bPx;
                      const minPx = 40;
                      const newAPx = Math.max(minPx, Math.min(total - minPx, start.aPx + deltaPx));
                      const ratio = newAPx / total;
                      const totalWeight = paneWeight(prev) + paneWeight(pane);
                      onResizePanes(
                        prev.id,
                        ratio * totalWeight,
                        pane.id,
                        (1 - ratio) * totalWeight,
                      );
                    }}
                    measure={() => {
                      const aEl = paneElsRef.current.get(prev.id);
                      const bEl = paneElsRef.current.get(pane.id);
                      if (!aEl || !bEl) return null;
                      return {
                        aPx: aEl.getBoundingClientRect().height,
                        bPx: bEl.getBoundingClientRect().height,
                      };
                    }}
                  />
                  {paneNode}
                </Fragment>
              );
            })}
          </div>
        );

        if (ci === 0) return colEl;
        const prevCol = layout.columns[ci - 1]!;
        return (
          <Fragment key={col.id}>
            <ResizeHandle
              axis="x"
              onResize={(deltaPx, start) => {
                const total = start.aPx + start.bPx;
                const minPx = 80;
                const newAPx = Math.max(minPx, Math.min(total - minPx, start.aPx + deltaPx));
                const ratio = newAPx / total;
                const totalWeight = columnWeight(prevCol) + columnWeight(col);
                onResizeColumns(prevCol.id, ratio * totalWeight, col.id, (1 - ratio) * totalWeight);
              }}
              measure={() => {
                const aEl = columnElsRef.current.get(prevCol.id);
                const bEl = columnElsRef.current.get(col.id);
                if (!aEl || !bEl) return null;
                return {
                  aPx: aEl.getBoundingClientRect().width,
                  bPx: bEl.getBoundingClientRect().width,
                };
              }}
            />
            {colEl}
          </Fragment>
        );
      })}
    </div>
  );
});
