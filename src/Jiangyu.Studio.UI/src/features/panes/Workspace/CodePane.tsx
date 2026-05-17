import { useCallback, useState } from "react";
import { ExternalLink, Maximize2, Minimize2, SplitSquareHorizontal, X } from "lucide-react";
import type { editor as monacoEditor } from "monaco-editor";
import { ContextMenu, type ContextMenuEntry } from "@shared/ui/ContextMenu/ContextMenu";
import { TabbedMonacoEditor } from "@features/editor/CodeEditor/TabbedMonacoEditor";
import { buildTabMenu } from "./tabMenu";
import type { CodePane as CodePaneModel } from "@features/panes/layout";
import { useEditorContent } from "@features/editor/content";
import { useLayoutStore } from "@features/panes/layoutStore";
import { DropOverlay } from "./DropOverlay";
import type { DropZone } from "./dropZone";
import { EmptyPrompt } from "./EmptyPrompt";
import { PANE_DRAG_MIME, TAB_DRAG_MIME, type TabDragPayload } from "./tabDrag";
import {
  CROSS_TAB_MIME,
  beginTabMove,
  completeTabMove,
  encodeCrossTabPayload,
  parseCrossTabPayload,
} from "@features/panes/crossTab";
import { CROSS_PANE_MIME } from "@features/panes/crossPane";
import { attachDragChip } from "@shared/drag/chip";
import { computeTabDropIndex } from "@shared/drag/computeDropIndex";
import { basename } from "@shared/path";
import styles from "./Workspace.module.css";

function getSelectedText(editor: monacoEditor.IStandaloneCodeEditor): string {
  const selection = editor.getSelection();
  const model = editor.getModel();
  if (!selection || !model || selection.isEmpty()) return "";
  return model.getValueInRange(selection);
}

async function copySelection(editor: monacoEditor.IStandaloneCodeEditor): Promise<void> {
  const text = getSelectedText(editor);
  if (text.length === 0) return;
  await navigator.clipboard.writeText(text);
}

async function cutSelection(editor: monacoEditor.IStandaloneCodeEditor): Promise<void> {
  const text = getSelectedText(editor);
  if (text.length === 0) return;
  await navigator.clipboard.writeText(text);
  const selection = editor.getSelection();
  if (!selection) return;
  editor.executeEdits("cut", [{ range: selection, text: "", forceMoveMarkers: true }]);
  editor.focus();
}

async function pasteAtCursor(editor: monacoEditor.IStandaloneCodeEditor): Promise<void> {
  const text = await navigator.clipboard.readText();
  if (text.length === 0) return;
  const selection = editor.getSelection();
  if (!selection) return;
  editor.executeEdits("paste", [{ range: selection, text, forceMoveMarkers: true }]);
  editor.focus();
}

// Languages whose Monaco build ships with a DocumentFormattingEditProvider.
// Others (C#, KDL, TOML, YAML, Python, …) would have the action registered but
// would no-op without an LSP backing it — so we hide the menu item instead.
const FORMATTABLE_LANGUAGES = new Set([
  "json",
  "jsonc",
  "javascript",
  "typescript",
  "html",
  "css",
  "scss",
  "less",
]);

function buildEditorMenu(editor: monacoEditor.IStandaloneCodeEditor): ContextMenuEntry[] {
  const items: ContextMenuEntry[] = [
    { label: "Cut", shortcut: "Ctrl+X", onSelect: () => void cutSelection(editor) },
    { label: "Copy", shortcut: "Ctrl+C", onSelect: () => void copySelection(editor) },
    { label: "Paste", shortcut: "Ctrl+V", onSelect: () => void pasteAtCursor(editor) },
  ];

  const language = editor.getModel()?.getLanguageId();
  const formatGroup: { id: string; shortcut?: string }[] = [];
  if (language !== undefined && FORMATTABLE_LANGUAGES.has(language)) {
    formatGroup.push({ id: "editor.action.formatDocument", shortcut: "Shift+Alt+F" });
  }
  const monacoGroups: { id: string; shortcut?: string }[][] = [
    [
      { id: "actions.find", shortcut: "Ctrl+F" },
      { id: "editor.action.startFindReplaceAction", shortcut: "Ctrl+H" },
    ],
    [{ id: "editor.action.commentLine", shortcut: "Ctrl+/" }],
    formatGroup,
  ];

  for (const group of monacoGroups) {
    const groupItems: ContextMenuEntry[] = [];
    for (const { id, shortcut } of group) {
      const action = editor.getAction(id);
      if (!action) continue;
      const onSelect = () => void action.run();
      groupItems.push(
        shortcut !== undefined
          ? { label: action.label, shortcut, onSelect }
          : { label: action.label, onSelect },
      );
    }
    if (groupItems.length === 0) continue;
    items.push("separator");
    items.push(...groupItems);
  }

  return items;
}

export interface CrossDragTicket {
  readonly paneId: string;
  readonly path: string;
}

interface CodePaneProps {
  pane: CodePaneModel;
  isActive: boolean;
  isFullscreen: boolean;
  flex: number;
  registerEl: (paneId: string, el: HTMLElement | null) => void;
  dragActive: boolean;
  onTabDragStart: () => void;
  onPaneDragStart: () => void;
  onPaneDrop: (toPaneId: string, zone: DropZone, event: React.DragEvent) => void;
  onTearOutPane: (paneId: string) => void;
  projectPath: string;
  onCrossDragStart: (ticket: CrossDragTicket) => void;
}

export function CodePane({
  pane,
  isActive,
  isFullscreen,
  flex,
  registerEl,
  dragActive,
  onTabDragStart,
  onPaneDragStart,
  onPaneDrop,
  onTearOutPane,
  projectPath,
  onCrossDragStart,
}: CodePaneProps) {
  const [splitMenu, setSplitMenu] = useState<{ x: number; y: number } | null>(null);
  const [tabbarDropHover, setTabbarDropHover] = useState(false);

  const activeFile = pane.activeTab;
  // Per-path selectors so keystrokes only re-render this pane (not siblings).
  const activeContent = useEditorContent((s) =>
    activeFile !== null ? s.contents[activeFile] : undefined,
  );
  const activeConflict = useEditorContent((s) =>
    activeFile !== null ? (s.conflicts[activeFile] ?? null) : null,
  );
  // The whole dirty set is read so the tab bar can dot every dirty tab. The
  // reference only changes when a path flips dirty/clean, so this isn't a
  // hot re-render.
  const dirtyFiles = useEditorContent((s) => s.dirty);

  const handleEditorMount = useCallback(
    (editor: monacoEditor.IStandaloneCodeEditor) => {
      useEditorContent.getState().mountEditor(pane.id, editor);
    },
    [pane.id],
  );

  const handleTabDragStart = useCallback(
    (e: React.DragEvent<HTMLButtonElement>, path: string) => {
      const payload: TabDragPayload = { fromPaneId: pane.id, path };
      e.dataTransfer.setData(TAB_DRAG_MIME, JSON.stringify(payload));
      // Also set a text/plain payload so cross-window drops (into another
      // Jiangyu window) can read it — custom mimetypes don't always bridge.
      e.dataTransfer.setData(CROSS_TAB_MIME, encodeCrossTabPayload(path));
      e.dataTransfer.effectAllowed = "move";
      attachDragChip(e, basename(path));
      onTabDragStart();
      onCrossDragStart({ paneId: pane.id, path });
      void beginTabMove(path).catch(() => {});
    },
    [pane.id, onTabDragStart, onCrossDragStart],
  );

  const handleTabbarDragStart = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      e.dataTransfer.setData(PANE_DRAG_MIME, JSON.stringify({ paneId: pane.id }));
      e.dataTransfer.effectAllowed = "move";
      const firstTab = pane.tabs[0];
      const label =
        firstTab === undefined
          ? "Empty pane"
          : pane.tabs.length === 1
            ? basename(firstTab.path)
            : `${pane.tabs.length} tabs`;
      attachDragChip(e, label);
      onPaneDragStart();
    },
    [pane.id, pane.tabs, onPaneDragStart],
  );

  const handleTabbarDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    const accepts =
      e.dataTransfer.types.includes(TAB_DRAG_MIME) || e.dataTransfer.types.includes(CROSS_TAB_MIME);
    if (!accepts) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    setTabbarDropHover(true);
  }, []);

  const handleTabbarDragLeave = useCallback(() => setTabbarDropHover(false), []);

  const handleTabbarDrop = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      setTabbarDropHover(false);
      // Prefer the in-window payload when present — richer, knows fromPaneId,
      // and avoids a host round-trip.
      const inWindow = e.dataTransfer.getData(TAB_DRAG_MIME);
      if (inWindow.length > 0) {
        e.preventDefault();
        try {
          const { fromPaneId, path } = JSON.parse(inWindow) as TabDragPayload;
          const store = useLayoutStore.getState();
          if (fromPaneId === pane.id) {
            const targetIndex = computeTabDropIndex(e.currentTarget, e.clientX);
            store.reorderTab(pane.id, path, targetIndex);
          } else {
            store.moveTab(fromPaneId, pane.id, path);
          }
        } catch {
          /* malformed — ignore */
        }
        return;
      }
      // Cross-window fallback: open the file in this pane and tell the host
      // so the source window can close its tab.
      const crossPath = parseCrossTabPayload(e.dataTransfer.getData(CROSS_TAB_MIME));
      if (crossPath === null) return;
      e.preventDefault();
      useLayoutStore.getState().openFile(crossPath, pane.id);
      void completeTabMove(crossPath).catch(() => {});
    },
    [pane.id],
  );

  const handleClosePaneDirect = useCallback(
    () => useLayoutStore.getState().closePane(pane.id),
    [pane.id],
  );

  const toolbar = (
    <>
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
        onClick={handleClosePaneDirect}
        title="Close pane"
      >
        <X size={12} />
      </button>
    </>
  );

  return (
    <>
      <TabbedMonacoEditor
        tabs={pane.tabs}
        activePath={activeFile}
        activeContent={activeContent}
        activeConflict={activeConflict}
        dirtyFiles={dirtyFiles}
        onSelectTab={(path) => useLayoutStore.getState().selectTab(pane.id, path)}
        onCloseTab={(path) => useLayoutStore.getState().closeTabsInPane(pane.id, [path])}
        onActiveContentChange={(value) => {
          if (activeFile === null) return;
          const store = useEditorContent.getState();
          store.setContent(activeFile, value);
          store.markDirty(activeFile, true);
        }}
        onSave={() => {
          if (activeFile !== null) void useEditorContent.getState().save(activeFile);
        }}
        onReload={() => {
          if (activeFile !== null) void useEditorContent.getState().reload(activeFile);
        }}
        onDismissConflict={() => {
          if (activeFile !== null) useEditorContent.getState().dismissConflict(activeFile);
        }}
        onEditorMount={handleEditorMount}
        onEditorFocus={() => useLayoutStore.getState().setActivePane(pane.id)}
        toolbar={toolbar}
        onTabbarDragStart={handleTabbarDragStart}
        tabDrag={{
          onTabDragStart: handleTabDragStart,
          onTabbarDragOver: handleTabbarDragOver,
          onTabbarDragLeave: handleTabbarDragLeave,
          onTabbarDrop: handleTabbarDrop,
          dropHover: tabbarDropHover,
        }}
        buildTabContextMenu={(path) =>
          buildTabMenu(path, pane.tabs, projectPath, (paths) =>
            useLayoutStore.getState().closeTabsInPane(pane.id, paths),
          )
        }
        buildEditorContextMenu={buildEditorMenu}
        dropOverlay={
          <DropOverlay
            active={dragActive}
            acceptedMimes={[TAB_DRAG_MIME, PANE_DRAG_MIME, CROSS_TAB_MIME, CROSS_PANE_MIME]}
            onDrop={(zone, e) => onPaneDrop(pane.id, zone, e)}
          />
        }
        emptyState={
          <EmptyPrompt
            onOpenBrowser={(kind) => useLayoutStore.getState().convertPane(pane.id, kind)}
          />
        }
        className={`${isActive ? styles.editorActive : ""} ${isFullscreen ? styles.editorFullscreen : ""}`.trim()}
        style={{ flex }}
        containerRef={(el) => registerEl(pane.id, el)}
        onMouseDown={() => {
          if (!isActive) useLayoutStore.getState().setActivePane(pane.id);
        }}
      />
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
    </>
  );
}
