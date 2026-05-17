import { useState } from "react";
import { ExternalLink, Maximize2, Minimize2, SplitSquareHorizontal, X } from "lucide-react";
import { BROWSER_KIND_META, type BrowserPane as BrowserPaneModel } from "@features/panes/layout";
import type { AssetBrowserState, TemplateBrowserState } from "@features/panes/browserState";
import { useLayoutStore } from "@features/panes/layoutStore";
import { AssetBrowser } from "@features/assets/AssetBrowser/AssetBrowser";
import { TemplateBrowser } from "@features/templates/TemplateBrowser/TemplateBrowser";
import { AgentPanel } from "@features/agent/AgentPanel/AgentPanel";
import { ContextMenu } from "@shared/ui/ContextMenu/ContextMenu";
import { DropOverlay } from "./DropOverlay";
import type { DropZone } from "./dropZone";
import { PANE_DRAG_MIME, TAB_DRAG_MIME, type PaneDragPayload } from "./tabDrag";
import { CROSS_TAB_MIME } from "@features/panes/crossTab";
import { CROSS_PANE_MIME } from "@features/panes/crossPane";
import { attachDragChip } from "@shared/drag/chip";
import styles from "./Workspace.module.css";

interface BrowserPaneProps {
  pane: BrowserPaneModel;
  projectPath: string;
  isActive: boolean;
  isFullscreen: boolean;
  flex: number;
  registerEl: (paneId: string, el: HTMLElement | null) => void;
  dragActive: boolean;
  fileEntries: readonly string[];
  lastCodePath: string | null;
  onPaneDragStart: () => void;
  onPaneDrop: (toPaneId: string, zone: DropZone, event: React.DragEvent) => void;
  onTearOutPane: (paneId: string) => void;
  onAppendToFile: (path: string, snippet: string) => Promise<void>;
  onRefreshFiles: () => void;
}

export function BrowserPane({
  pane,
  projectPath,
  isActive,
  isFullscreen,
  flex,
  registerEl,
  dragActive,
  fileEntries,
  lastCodePath,
  onPaneDragStart,
  onPaneDrop,
  onTearOutPane,
  onAppendToFile,
  onRefreshFiles,
}: BrowserPaneProps) {
  const meta = BROWSER_KIND_META[pane.kind];
  const [splitMenu, setSplitMenu] = useState<{ x: number; y: number } | null>(null);

  const handleHeaderDragStart = (e: React.DragEvent) => {
    const payload: PaneDragPayload = { paneId: pane.id };
    e.dataTransfer.setData(PANE_DRAG_MIME, JSON.stringify(payload));
    e.dataTransfer.effectAllowed = "move";
    attachDragChip(e, meta.label);
    onPaneDragStart();
  };

  const setActive = () => {
    if (!isActive) useLayoutStore.getState().setActivePane(pane.id);
  };

  return (
    <div
      ref={(el) => registerEl(pane.id, el)}
      className={`${styles.editor} ${isActive ? styles.editorActive : ""} ${isFullscreen ? styles.editorFullscreen : ""}`}
      style={{ flex }}
      role="presentation"
      onMouseDown={setActive}
    >
      <div className={styles.tabbar}>
        <div className={styles.tabScroll}>
          <div
            className={styles.paneHeaderItem}
            draggable
            onDragStart={handleHeaderDragStart}
            title="Drag to move pane"
          >
            <span className={styles.paneHeaderLabel}>{meta.label}</span>
          </div>
          <div
            className={styles.tabFill}
            draggable
            onDragStart={handleHeaderDragStart}
            title="Drag to move pane"
          />
        </div>
        <div className={styles.tabActions}>
          <button
            type="button"
            className={styles.tabbarButton}
            onClick={(e) => {
              const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
              setSplitMenu({ x: rect.left, y: rect.bottom });
            }}
            title="Split pane"
          >
            <SplitSquareHorizontal size={12} />
          </button>
          <button
            type="button"
            className={styles.tabbarButton}
            onClick={() => onTearOutPane(pane.id)}
            title="Move pane to new window"
          >
            <ExternalLink size={12} />
          </button>
          <button
            type="button"
            className={styles.tabbarButton}
            onClick={() => useLayoutStore.getState().toggleFullscreen(pane.id)}
            title={isFullscreen ? "Exit fullscreen (Esc)" : "Fullscreen pane"}
          >
            {isFullscreen ? <Minimize2 size={12} /> : <Maximize2 size={12} />}
          </button>
          <button
            type="button"
            className={styles.tabbarButton}
            onClick={() => useLayoutStore.getState().closePane(pane.id)}
            title="Close pane"
          >
            <X size={12} />
          </button>
        </div>
      </div>
      <div className={styles.content}>
        {pane.kind === "assetBrowser" ? (
          <AssetBrowser
            projectPath={projectPath}
            initialState={pane.state as AssetBrowserState | undefined}
            onStateChange={(state) => useLayoutStore.getState().setBrowserPaneState(pane.id, state)}
          />
        ) : pane.kind === "agent" ? (
          <AgentPanel />
        ) : (
          <TemplateBrowser
            projectPath={projectPath}
            fileEntries={fileEntries}
            lastCodePath={lastCodePath}
            onOpenFile={(path) => useLayoutStore.getState().openFile(path)}
            onAppendToFile={onAppendToFile}
            onRefreshFiles={onRefreshFiles}
            initialState={pane.state as TemplateBrowserState | undefined}
            onStateChange={(state) => useLayoutStore.getState().setBrowserPaneState(pane.id, state)}
          />
        )}
        <DropOverlay
          active={dragActive}
          acceptedMimes={[TAB_DRAG_MIME, PANE_DRAG_MIME, CROSS_TAB_MIME, CROSS_PANE_MIME]}
          onDrop={(zone, e) => onPaneDrop(pane.id, zone, e)}
        />
      </div>
      {splitMenu && (
        <ContextMenu
          x={splitMenu.x}
          y={splitMenu.y}
          items={[
            {
              label: "Split right",
              onSelect: () => useLayoutStore.getState().splitFromPane(pane.id, "right"),
            },
            {
              label: "Split down",
              onSelect: () => useLayoutStore.getState().splitFromPane(pane.id, "down"),
            },
          ]}
          onClose={() => setSplitMenu(null)}
        />
      )}
    </div>
  );
}
