import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { PALETTE_SCOPE, useRegisterActions, type PaletteAction } from "@lib/palette/actions.ts";
import { columnWeight, getActiveCodePane, paneWeight, type Layout } from "@lib/layout.ts";
import { fileTargetCommands } from "@lib/project/fileCommands.ts";
import { useEditorContent } from "@lib/editor/content.ts";
import { useLayoutStore } from "@lib/panes/layoutStore.ts";
import { CodePane } from "./CodePane.tsx";
import { BrowserPane } from "./BrowserPane.tsx";
import { EmptyPrompt } from "./EmptyPrompt.tsx";
import { ResizeHandle } from "./ResizeHandle.tsx";
import type { DropZone } from "./dropZone.ts";
import { parseCrossTabPayload, completeTabMove, CROSS_TAB_MIME } from "@lib/drag/crossTab.ts";
import { CROSS_PANE_MIME, completePaneMove, parseCrossPanePayload } from "@lib/drag/crossPane.ts";
import { isTemplateDragPayload } from "@lib/drag/templateDrag.ts";
import {
  PANE_DRAG_MIME,
  TAB_DRAG_MIME,
  type PaneDragPayload,
  type TabDragPayload,
} from "./tabDrag.ts";
import { isBinaryFile } from "@components/CodeEditor/fileTypes.ts";
import { isUsefulMonacoAction } from "@components/CodeEditor/monacoActionFilter.ts";
import styles from "./Workspace.module.css";

interface WorkspaceGridProps {
  /** The open project's root. Used for file-relative palette commands. */
  projectPath: string;
  /** Layout tree, subscribed by App and passed down so we re-render on changes. */
  layout: Layout;
  fullscreenPaneId: string | null;
  fileEntries: readonly string[];
  lastCodePath: string | null;
  /** Tear-out requires the pane-window store; App owns that side. */
  onTearOutPane: (paneId: string) => void;
  /** Cross-window tab drag ticket registration, held in App for the tabMovedOut handler. */
  onCrossDragStart: (ticket: { paneId: string; path: string }) => void;
  /** Template-snippet helper (opens the file then delegates to the editor content store). */
  onAppendToFile: (path: string, snippet: string) => Promise<void>;
  onRefreshFiles: () => void;
}

export function WorkspaceGrid({
  projectPath,
  layout,
  fullscreenPaneId,
  fileEntries,
  lastCodePath,
  onTearOutPane,
  onCrossDragStart,
  onAppendToFile,
  onRefreshFiles,
}: WorkspaceGridProps) {
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
  // are shown only during a drag, so they don't block ordinary clicks or
  // Monaco interaction the rest of the time.
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

  // Activate the drop overlays when an external drag carrying a Jiangyu
  // payload enters the window (e.g. cross-window drag from a secondary, or
  // a sidebar file drop that bubbles up to document level). In-window drags
  // set dragActive directly via handleDragStart.
  useEffect(() => {
    const onEnter = (e: DragEvent) => {
      const types = e.dataTransfer?.types;
      if (types === undefined) return;
      const arr = Array.from(types);
      // Template-browser drags target the visual editor, not pane splits.
      // Tag MIME is reliable same-window; cross-window we best-effort inspect
      // text/plain to detect the template payload marker.
      if (e.dataTransfer !== null && isTemplateDragPayload(e.dataTransfer)) return;
      if (arr.includes(CROSS_TAB_MIME) || arr.includes(CROSS_PANE_MIME)) {
        setDragActive(true);
      }
    };
    document.addEventListener("dragenter", onEnter);
    return () => document.removeEventListener("dragenter", onEnter);
  }, []);

  const handleDragStart = useCallback(() => setDragActive(true), []);

  const handlePaneDrop = useCallback((toPaneId: string, zone: DropZone, e: React.DragEvent) => {
    setDragActive(false);
    const store = useLayoutStore.getState();
    const tabRaw = e.dataTransfer.getData(TAB_DRAG_MIME);
    if (tabRaw.length > 0) {
      try {
        const { fromPaneId, path } = JSON.parse(tabRaw) as TabDragPayload;
        if (zone === "centre") store.moveTab(fromPaneId, toPaneId, path);
        else store.splitWithTab(fromPaneId, toPaneId, path, zone);
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
        if (zone === "centre") store.swapPanes(paneId, toPaneId);
        else store.movePaneToEdge(paneId, toPaneId, zone);
      } catch {
        // ignore malformed payload
      }
      return;
    }
    // Cross-window PANE drag: centre is coerced to "right" because "merge
    // into this pane" doesn't map cleanly across mixed kinds.
    const crossPaneRaw = e.dataTransfer.getData(CROSS_PANE_MIME);
    if (crossPaneRaw.length > 0) {
      const payload = parseCrossPanePayload(crossPaneRaw);
      if (payload !== null) {
        const edge = zone === "centre" ? "right" : zone;
        store.insertCrossWindowPane(toPaneId, payload, edge);
        void completePaneMove().catch(() => {});
      }
      return;
    }
    // Cross-window tab drag or sidebar file drag. completeTabMove is a
    // no-op for sidebar drags (no matching beginTabMove state) and
    // triggers close-at-source for cross-window tab drags.
    const crossPath = parseCrossTabPayload(e.dataTransfer.getData(CROSS_TAB_MIME));
    if (crossPath !== null) {
      if (zone === "centre") store.openFile(crossPath, toPaneId);
      else store.splitAtEdgeWithPath(toPaneId, crossPath, zone);
      void completeTabMove(crossPath).catch(() => {});
    }
  }, []);

  // Single walk over the layout yields every derived set the grid needs.
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

  // Lazy-load content for every active tab that isn't cached yet.
  useEffect(() => {
    for (const path of activePaths) {
      if (isBinaryFile(path)) continue;
      void useEditorContent.getState().loadContent(path);
    }
  }, [activePaths]);

  // Drop stale store entries when tabs/panes close.
  useEffect(() => {
    useEditorContent.getState().prune(openPaths, openPaneIds);
  }, [openPaths, openPaneIds]);

  // Monaco's automaticLayout ResizeObserver misses sibling-column removals
  // (weight redistribution happens in the same frame as the DOM mutation).
  // Force every live editor to re-measure after any topology / weight change.
  useEffect(() => {
    const editors = useEditorContent.getState().editors;
    const id = requestAnimationFrame(() => {
      for (const editor of Object.values(editors)) editor.layout();
    });
    return () => cancelAnimationFrame(id);
  }, [layout, fullscreenPaneId]);

  // Active-pane palette actions: save/close + Monaco actions of the focused
  // editor. The editor selector reads the current pane's instance from the
  // store; re-subscribing only when the active pane id changes avoids noise
  // while the user is typing.
  const activeCodePane = getActiveCodePane(layout);
  const activeFile = activeCodePane?.activeTab ?? null;
  const activeEditor = useEditorContent((s) =>
    activeCodePane !== null ? (s.editors[activeCodePane.id] ?? null) : null,
  );

  const fileActions = useMemo<PaletteAction[]>(() => {
    if (activeCodePane === null || activeFile === null) return [];
    const paneId = activeCodePane.id;
    const commands = fileTargetCommands(activeFile, projectPath, (paths) =>
      useLayoutStore.getState().closeTabsInPane(paneId, paths),
    );
    const close = commands.find((c) => c.id === "close");
    if (close === undefined) return [];
    const extras = commands.filter((c) => c.id !== "close");

    const result: PaletteAction[] = [
      {
        id: "editor.save",
        label: "Save",
        scope: PALETTE_SCOPE.File,
        cn: "保存",
        kbd: "Ctrl+S",
        run: () => void useEditorContent.getState().save(activeFile),
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
  }, [activeCodePane, activeFile, projectPath]);

  const monacoActions = useMemo<PaletteAction[]>(() => {
    if (!activeEditor) return [];
    // Monaco doesn't expose a public way to read an action's keybinding; the
    // standalone editor keeps it on _standaloneKeybindingService. We tap that
    // to show shortcut labels next to each command, VS Code style.
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
          <EmptyPrompt onOpenBrowser={(kind) => useLayoutStore.getState().splitRight(kind)} />
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
                  <CodePane
                    pane={pane}
                    isActive={isActive}
                    isFullscreen={isFullscreen}
                    flex={paneWeight(pane)}
                    registerEl={registerPaneEl}
                    dragActive={dragActive}
                    onTabDragStart={handleDragStart}
                    onPaneDragStart={handleDragStart}
                    onPaneDrop={handlePaneDrop}
                    onTearOutPane={onTearOutPane}
                    projectPath={projectPath}
                    onCrossDragStart={onCrossDragStart}
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
                    onPaneDragStart={handleDragStart}
                    onPaneDrop={handlePaneDrop}
                    onTearOutPane={onTearOutPane}
                    onAppendToFile={onAppendToFile}
                    onRefreshFiles={onRefreshFiles}
                  />
                );
              if (pi === 0) return <Fragment key={pane.id}>{paneNode}</Fragment>;
              const prev = col.panes[pi - 1];
              if (prev === undefined) return <Fragment key={pane.id}>{paneNode}</Fragment>;
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
                      useLayoutStore
                        .getState()
                        .resizePanes(
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
        const prevCol = layout.columns[ci - 1];
        if (prevCol === undefined) return colEl;
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
                useLayoutStore
                  .getState()
                  .resizeColumns(
                    prevCol.id,
                    ratio * totalWeight,
                    col.id,
                    (1 - ratio) * totalWeight,
                  );
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
