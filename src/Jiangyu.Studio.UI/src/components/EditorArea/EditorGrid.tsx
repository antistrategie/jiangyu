import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from "react";
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
  PANE_DRAG_MIME,
  TAB_DRAG_MIME,
  type PaneDragPayload,
  type TabDragPayload,
} from "./tabDrag.ts";
import { isBinaryFile } from "./fileTypes.ts";
import { isUsefulMonacoAction } from "./monacoActionFilter.ts";
import styles from "./EditorArea.module.css";

type ConflictKind = FileChangeKind;

interface EditorGridProps {
  projectPath: string;
  layout: Layout;
  fullscreenPaneId: string | null;
  dirtyFiles: Set<string>;
  onSelectTab: (paneId: string, path: string) => void;
  onSetActivePane: (paneId: string) => void;
  onCloseTabsInPane: (paneId: string, paths: readonly string[]) => void;
  onClosePane: (paneId: string) => void;
  onMoveTab: (fromPaneId: string, toPaneId: string, path: string) => void;
  onMarkDirty: (path: string, isDirty: boolean) => void;
  onOpenBrowserPane: (kind: BrowserPaneModel["kind"]) => void;
  onResizeColumns: (
    leftId: string,
    leftWeight: number,
    rightId: string,
    rightWeight: number,
  ) => void;
  onResizePanes: (topId: string, topWeight: number, bottomId: string, bottomWeight: number) => void;
  onSplitWithTab: (fromPaneId: string, toPaneId: string, path: string, edge: SplitEdge) => void;
  onMovePaneToEdge: (paneId: string, targetPaneId: string, edge: SplitEdge) => void;
  onConvertPane: (paneId: string, kind: BrowserPaneModel["kind"]) => void;
  onSplitFromPane: (paneId: string, direction: "right" | "down") => void;
  onToggleFullscreen: (paneId: string) => void;
}

export function EditorGrid({
  projectPath,
  layout,
  fullscreenPaneId,
  dirtyFiles,
  onSelectTab,
  onSetActivePane,
  onCloseTabsInPane,
  onClosePane,
  onMoveTab,
  onMarkDirty,
  onOpenBrowserPane,
  onResizeColumns,
  onResizePanes,
  onSplitWithTab,
  onMovePaneToEdge,
  onConvertPane,
  onSplitFromPane,
  onToggleFullscreen,
}: EditorGridProps) {
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
          if (zone === "centre") return; // pane-into-pane merge not supported
          onMovePaneToEdge(paneId, toPaneId, zone);
        } catch {
          // ignore malformed payload
        }
      }
    },
    [onMoveTab, onSplitWithTab, onMovePaneToEdge],
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

  // Lazy-load any path that is an active tab somewhere and isn't already cached.
  const activePaths = useMemo(() => {
    const out = new Set<string>();
    for (const col of layout.columns) {
      for (const pane of col.panes) {
        if (pane.kind !== "code") continue;
        if (pane.activeTab !== null) out.add(pane.activeTab);
      }
    }
    return out;
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

  // Drop content / conflict / editor entries when their path leaves all panes.
  const openPaths = useMemo(() => {
    const out = new Set<string>();
    for (const col of layout.columns) {
      for (const pane of col.panes) {
        if (pane.kind !== "code") continue;
        for (const tab of pane.tabs) out.add(tab.path);
      }
    }
    return out;
  }, [layout]);
  const openPaneIds = useMemo(() => {
    const out = new Set<string>();
    for (const col of layout.columns) for (const pane of col.panes) out.add(pane.id);
    return out;
  }, [layout]);

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
                    onSetActive={onSetActivePane}
                    onClosePane={onClosePane}
                    onPaneDragStart={handleDragStart}
                    onPaneDrop={handlePaneDrop}
                    onSplitFromPane={onSplitFromPane}
                    onToggleFullscreen={onToggleFullscreen}
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
}
