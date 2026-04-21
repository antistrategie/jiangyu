import { Maximize2, Minimize2, SplitSquareHorizontal, SplitSquareVertical, X } from "lucide-react";
import { BROWSER_KIND_META, type BrowserPane as BrowserPaneModel } from "../../lib/layout.ts";
import { AssetBrowser } from "../AssetBrowser/AssetBrowser.tsx";
import { DropOverlay, type DropZone } from "./DropOverlay.tsx";
import { PANE_DRAG_MIME, TAB_DRAG_MIME, type PaneDragPayload } from "./tabDrag.ts";
import styles from "./EditorArea.module.css";

interface BrowserPaneProps {
  pane: BrowserPaneModel;
  projectPath: string;
  isActive: boolean;
  isFullscreen: boolean;
  flex: number;
  registerEl: (paneId: string, el: HTMLElement | null) => void;
  dragActive: boolean;
  onSetActive: (paneId: string) => void;
  onClosePane: (paneId: string) => void;
  onPaneDragStart: () => void;
  onPaneDrop: (toPaneId: string, zone: DropZone, event: React.DragEvent) => void;
  onSplitFromPane: (paneId: string, direction: "right" | "down") => void;
  onToggleFullscreen: (paneId: string) => void;
}

export function BrowserPane({
  pane,
  projectPath,
  isActive,
  isFullscreen,
  flex,
  registerEl,
  dragActive,
  onSetActive,
  onClosePane,
  onPaneDragStart,
  onPaneDrop,
  onSplitFromPane,
  onToggleFullscreen,
}: BrowserPaneProps) {
  const meta = BROWSER_KIND_META[pane.kind];

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
            onClick={() => onSplitFromPane(pane.id, "right")}
            title="Split right"
          >
            <SplitSquareHorizontal size={12} />
          </button>
          <button
            type="button"
            className={styles.tabbarButton}
            onClick={() => onSplitFromPane(pane.id, "down")}
            title="Split down"
          >
            <SplitSquareVertical size={12} />
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
          <AssetBrowser projectPath={projectPath} />
        ) : (
          <p className={styles.empty}>{meta.label} — coming soon</p>
        )}
        <DropOverlay
          active={dragActive}
          acceptedMimes={[TAB_DRAG_MIME, PANE_DRAG_MIME]}
          onDrop={(zone, e) => onPaneDrop(pane.id, zone, e)}
        />
      </div>
    </div>
  );
}
