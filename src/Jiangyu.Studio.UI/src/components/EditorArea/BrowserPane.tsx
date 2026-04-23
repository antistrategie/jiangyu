import { useState } from "react";
import { ExternalLink, Maximize2, Minimize2, SplitSquareHorizontal, X } from "lucide-react";
import { BROWSER_KIND_META, type BrowserPane as BrowserPaneModel } from "../../lib/layout.ts";
import type { AssetBrowserState, TemplateBrowserState } from "../../lib/browserState.ts";
import { AssetBrowser } from "../AssetBrowser/AssetBrowser.tsx";
import { TemplateBrowser } from "../TemplateBrowser/TemplateBrowser.tsx";
import { ContextMenu } from "../ContextMenu/ContextMenu.tsx";
import { DropOverlay, type DropZone } from "./DropOverlay.tsx";
import { PANE_DRAG_MIME, TAB_DRAG_MIME, type PaneDragPayload } from "./tabDrag.ts";
import { CROSS_TAB_MIME } from "../../lib/crossWindowTabDrag.ts";
import { CROSS_PANE_MIME } from "../../lib/crossWindowPaneDrag.ts";
import styles from "./EditorArea.module.css";

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
  onSetActive: (paneId: string) => void;
  onClosePane: (paneId: string) => void;
  onPaneDragStart: () => void;
  onPaneDrop: (toPaneId: string, zone: DropZone, event: React.DragEvent) => void;
  onSplitFromPane: (paneId: string, direction: "right" | "down") => void;
  onToggleFullscreen: (paneId: string) => void;
  onTearOutPane: (paneId: string) => void;
  onOpenFile: (path: string) => void;
  onAppendToFile: (path: string, snippet: string) => Promise<void>;
  onRefreshFiles: () => void;
  onBrowserStateChange: (paneId: string, state: AssetBrowserState | TemplateBrowserState) => void;
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
  onSetActive,
  onClosePane,
  onPaneDragStart,
  onPaneDrop,
  onSplitFromPane,
  onToggleFullscreen,
  onTearOutPane,
  onOpenFile,
  onAppendToFile,
  onRefreshFiles,
  onBrowserStateChange,
}: BrowserPaneProps) {
  const meta = BROWSER_KIND_META[pane.kind];
  const [splitMenu, setSplitMenu] = useState<{ x: number; y: number } | null>(null);

  const handleHeaderDragStart = (e: React.DragEvent) => {
    const payload: PaneDragPayload = { paneId: pane.id };
    e.dataTransfer.setData(PANE_DRAG_MIME, JSON.stringify(payload));
    e.dataTransfer.effectAllowed = "move";
    onPaneDragStart();
  };

  return (
    <div
      ref={(el) => registerEl(pane.id, el)}
      className={`${styles.editor} ${isActive ? styles.editorActive : ""} ${isFullscreen ? styles.editorFullscreen : ""}`}
      style={{ flex }}
      onMouseDown={() => {
        if (!isActive) onSetActive(pane.id);
      }}
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
            onClick={() => onToggleFullscreen(pane.id)}
            title={isFullscreen ? "Exit fullscreen (Esc)" : "Fullscreen pane"}
          >
            {isFullscreen ? <Minimize2 size={12} /> : <Maximize2 size={12} />}
          </button>
          <button
            type="button"
            className={styles.tabbarButton}
            onClick={() => onClosePane(pane.id)}
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
            onStateChange={(state) => onBrowserStateChange(pane.id, state)}
          />
        ) : pane.kind === "templateBrowser" ? (
          <TemplateBrowser
            projectPath={projectPath}
            fileEntries={fileEntries}
            lastCodePath={lastCodePath}
            onOpenFile={onOpenFile}
            onAppendToFile={onAppendToFile}
            onRefreshFiles={onRefreshFiles}
            initialState={pane.state as TemplateBrowserState | undefined}
            onStateChange={(state) => onBrowserStateChange(pane.id, state)}
          />
        ) : (
          <p className={styles.empty}>{meta.label} — coming soon</p>
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
            { label: "Split right", onSelect: () => onSplitFromPane(pane.id, "right") },
            { label: "Split down", onSelect: () => onSplitFromPane(pane.id, "down") },
          ]}
          onClose={() => setSplitMenu(null)}
        />
      )}
    </div>
  );
}
