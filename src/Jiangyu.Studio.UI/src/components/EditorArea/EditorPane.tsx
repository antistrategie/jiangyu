import { useCallback, useState } from "react";
import { ExternalLink, Maximize2, Minimize2, SplitSquareHorizontal, X } from "lucide-react";
import type { editor as monacoEditor } from "monaco-editor";
import { ContextMenu, type ContextMenuEntry } from "../ContextMenu/ContextMenu.tsx";
import { TabbedMonacoEditor } from "../CodeEditor/TabbedMonacoEditor.tsx";
import { buildTabMenu } from "./tabMenu.ts";
import type { CodePane } from "../../lib/layout.ts";
import type { FileChangeKind } from "../../lib/rpc.ts";
import { DropOverlay, type DropZone } from "./DropOverlay.tsx";
import { EmptyPrompt } from "./EmptyPrompt.tsx";
import type { BrowserPane as BrowserPaneModel } from "../../lib/layout.ts";
import { PANE_DRAG_MIME, TAB_DRAG_MIME, type TabDragPayload } from "./tabDrag.ts";
import {
  CROSS_TAB_MIME,
  beginTabMove,
  completeTabMove,
  encodeCrossTabPayload,
  parseCrossTabPayload,
} from "../../lib/crossWindowTabDrag.ts";
import { CROSS_PANE_MIME } from "../../lib/crossWindowPaneDrag.ts";
import styles from "./EditorArea.module.css";

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

  const monacoGroups: { id: string; shortcut?: string }[][] = [
    [
      { id: "actions.find", shortcut: "Ctrl+F" },
      { id: "editor.action.startFindReplaceAction", shortcut: "Ctrl+H" },
    ],
    [{ id: "editor.action.commentLine", shortcut: "Ctrl+/" }],
    [],
  ];

  const language = editor.getModel()?.getLanguageId();
  if (language !== undefined && FORMATTABLE_LANGUAGES.has(language)) {
    monacoGroups[2]!.push({ id: "editor.action.formatDocument", shortcut: "Shift+Alt+F" });
  }

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

interface EditorPaneProps {
  pane: CodePane;
  isActive: boolean;
  isFullscreen: boolean;
  flex: number;
  registerEl: (paneId: string, el: HTMLElement | null) => void;
  dragActive: boolean;
  onTabDragStart: () => void;
  onPaneDragStart: () => void;
  onPaneDrop: (toPaneId: string, zone: DropZone, event: React.DragEvent) => void;
  onConvertPane: (paneId: string, kind: BrowserPaneModel["kind"]) => void;
  onSplitFromPane: (paneId: string, direction: "right" | "down") => void;
  onClosePane: (paneId: string) => void;
  onToggleFullscreen: (paneId: string) => void;
  onTearOutPane: (paneId: string) => void;
  projectPath: string;
  dirtyFiles: Set<string>;
  contents: ReadonlyMap<string, string>;
  conflicts: ReadonlyMap<string, FileChangeKind>;
  onSelectTab: (paneId: string, path: string) => void;
  onSetActive: (paneId: string) => void;
  onCloseTabs: (paneId: string, paths: readonly string[]) => void;
  onMoveTab: (fromPaneId: string, toPaneId: string, path: string) => void;
  onContentChange: (path: string, value: string) => void;
  onEditorMount: (paneId: string, editor: monacoEditor.IStandaloneCodeEditor) => void;
  onSave: (path: string) => Promise<void> | void;
  onReload: (path: string) => Promise<void> | void;
  onDismissConflict: (path: string) => void;
  onCrossDragStart: (ticket: CrossDragTicket) => void;
  onOpenFileInPane: (paneId: string, path: string) => void;
}

export function EditorPane({
  pane,
  isActive,
  isFullscreen,
  flex,
  registerEl,
  dragActive,
  onTabDragStart,
  onPaneDragStart,
  onPaneDrop,
  onConvertPane,
  onSplitFromPane,
  onClosePane,
  onToggleFullscreen,
  onTearOutPane,
  projectPath,
  dirtyFiles,
  contents,
  conflicts,
  onSelectTab,
  onSetActive,
  onCloseTabs,
  onMoveTab,
  onContentChange,
  onEditorMount,
  onSave,
  onReload,
  onDismissConflict,
  onCrossDragStart,
  onOpenFileInPane,
}: EditorPaneProps) {
  const [splitMenu, setSplitMenu] = useState<{ x: number; y: number } | null>(null);
  const [tabbarDropHover, setTabbarDropHover] = useState(false);

  const activeFile = pane.activeTab;
  const activeContent = activeFile !== null ? contents.get(activeFile) : undefined;
  const activeConflict = activeFile !== null ? (conflicts.get(activeFile) ?? null) : null;

  const handleEditorMount = useCallback(
    (editor: monacoEditor.IStandaloneCodeEditor) => {
      onEditorMount(pane.id, editor);
    },
    [pane.id, onEditorMount],
  );

  const handleTabDragStart = useCallback(
    (e: React.DragEvent<HTMLButtonElement>, path: string) => {
      const payload: TabDragPayload = { fromPaneId: pane.id, path };
      e.dataTransfer.setData(TAB_DRAG_MIME, JSON.stringify(payload));
      // Also set a text/plain payload so cross-window drops (into another
      // Jiangyu window) can read it — custom mimetypes don't always bridge.
      e.dataTransfer.setData(CROSS_TAB_MIME, encodeCrossTabPayload(path));
      e.dataTransfer.effectAllowed = "move";
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
      onPaneDragStart();
    },
    [pane.id, onPaneDragStart],
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
          if (fromPaneId === pane.id) return;
          onMoveTab(fromPaneId, pane.id, path);
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
      onOpenFileInPane(pane.id, crossPath);
      void completeTabMove(crossPath).catch(() => {});
    },
    [pane.id, onMoveTab, onOpenFileInPane],
  );

  const handleClosePaneDirect = useCallback(() => onClosePane(pane.id), [onClosePane, pane.id]);

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
        onClick={() => onToggleFullscreen(pane.id)}
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
        onSelectTab={(path) => onSelectTab(pane.id, path)}
        onCloseTab={(path) => onCloseTabs(pane.id, [path])}
        onActiveContentChange={(value) => {
          if (activeFile !== null) onContentChange(activeFile, value);
        }}
        onSave={() => {
          if (activeFile !== null) void onSave(activeFile);
        }}
        onReload={() => {
          if (activeFile !== null) void onReload(activeFile);
        }}
        onDismissConflict={() => {
          if (activeFile !== null) onDismissConflict(activeFile);
        }}
        onEditorMount={handleEditorMount}
        onEditorFocus={() => onSetActive(pane.id)}
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
          buildTabMenu(path, pane.tabs, projectPath, (paths) => onCloseTabs(pane.id, paths))
        }
        buildEditorContextMenu={buildEditorMenu}
        dropOverlay={
          <DropOverlay
            active={dragActive}
            acceptedMimes={[TAB_DRAG_MIME, PANE_DRAG_MIME, CROSS_TAB_MIME, CROSS_PANE_MIME]}
            onDrop={(zone, e) => onPaneDrop(pane.id, zone, e)}
          />
        }
        emptyState={<EmptyPrompt onOpenBrowser={(kind) => onConvertPane(pane.id, kind)} />}
        className={`${isActive ? styles.editorActive : ""} ${isFullscreen ? styles.editorFullscreen : ""}`.trim()}
        style={{ flex }}
        containerRef={(el) => registerEl(pane.id, el)}
        onMouseDown={() => {
          if (!isActive) onSetActive(pane.id);
        }}
      />
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
    </>
  );
}
