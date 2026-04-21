import { useCallback, useRef, useState } from "react";
import { Maximize2, Minimize2, SplitSquareHorizontal, SplitSquareVertical, X } from "lucide-react";
import Editor, { type OnChange, type OnMount } from "@monaco-editor/react";
import type { editor as monacoEditor } from "monaco-editor";
import { jiangyuTheme } from "./jiangyuTheme.ts";
import { ContextMenu, type ContextMenuEntry } from "../ContextMenu/ContextMenu.tsx";
import { buildTabMenu } from "./tabMenu.ts";
import { getLanguage, isBinaryFile } from "./fileTypes.ts";
import type { CodePane } from "../../lib/layout.ts";
import type { FileChangeKind } from "../../lib/rpc.ts";
import { DropOverlay, type DropZone } from "./DropOverlay.tsx";
import { EmptyPrompt } from "./EmptyPrompt.tsx";
import type { BrowserPane as BrowserPaneModel } from "../../lib/layout.ts";
import { PANE_DRAG_MIME, TAB_DRAG_MIME, type TabDragPayload } from "./tabDrag.ts";
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
}: EditorPaneProps) {
  const [editor, setEditor] = useState<monacoEditor.IStandaloneCodeEditor | null>(null);
  const [editorMenu, setEditorMenu] = useState<{ x: number; y: number } | null>(null);
  const [tabMenu, setTabMenu] = useState<{ x: number; y: number; path: string } | null>(null);
  const [tabbarDropHover, setTabbarDropHover] = useState(false);
  const themeRegistered = useRef(false);

  const activeFile = pane.activeTab;
  const activeContent = activeFile !== null ? contents.get(activeFile) : undefined;
  const activeConflict = activeFile !== null ? (conflicts.get(activeFile) ?? null) : null;
  const isBinary = activeFile !== null ? isBinaryFile(activeFile) : false;
  const isLoading = activeFile !== null && !isBinary && activeContent === undefined;

  const handleMount: OnMount = useCallback(
    (editor, monaco) => {
      setEditor(editor);
      onEditorMount(pane.id, editor);
      if (!themeRegistered.current) {
        monaco.editor.defineTheme("jiangyu", jiangyuTheme);
        themeRegistered.current = true;
      }
      monaco.editor.setTheme("jiangyu");
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        const path = pane.activeTab;
        if (path !== null) void onSave(path);
      });
      editor.onDidFocusEditorWidget(() => {
        onSetActive(pane.id);
      });
      editor.onDidDispose(() => setEditor(null));
    },
    [pane.id, pane.activeTab, onEditorMount, onSave, onSetActive],
  );

  const handleChange: OnChange = useCallback(
    (value) => {
      if (value === undefined || activeFile === null) return;
      onContentChange(activeFile, value);
    },
    [activeFile, onContentChange],
  );

  const handleTabDragStart = (e: React.DragEvent, path: string) => {
    const payload: TabDragPayload = { fromPaneId: pane.id, path };
    e.dataTransfer.setData(TAB_DRAG_MIME, JSON.stringify(payload));
    e.dataTransfer.effectAllowed = "move";
    onTabDragStart();
  };

  const handleTabbarDragStart = (e: React.DragEvent) => {
    e.dataTransfer.setData(PANE_DRAG_MIME, JSON.stringify({ paneId: pane.id }));
    e.dataTransfer.effectAllowed = "move";
    onPaneDragStart();
  };

  const handleTabbarDragOver = (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(TAB_DRAG_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    setTabbarDropHover(true);
  };

  const handleTabbarDragLeave = () => setTabbarDropHover(false);

  const handleTabbarDrop = (e: React.DragEvent) => {
    setTabbarDropHover(false);
    const raw = e.dataTransfer.getData(TAB_DRAG_MIME);
    if (raw.length === 0) return;
    e.preventDefault();
    try {
      const { fromPaneId, path } = JSON.parse(raw) as TabDragPayload;
      if (fromPaneId === pane.id) return;
      onMoveTab(fromPaneId, pane.id, path);
    } catch {
      // Malformed payload — ignore.
    }
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
      <div className={`${styles.tabbar} ${tabbarDropHover ? styles.tabbarDrop : ""}`}>
        <div
          className={styles.tabScroll}
          onWheel={(e) => {
            if (e.deltaY === 0) return;
            e.currentTarget.scrollLeft += e.deltaY;
          }}
          onDragOver={handleTabbarDragOver}
          onDragLeave={handleTabbarDragLeave}
          onDrop={handleTabbarDrop}
        >
          {pane.tabs.map((tab) => (
            <button
              key={tab.path}
              className={`${styles.tab} ${tab.path === activeFile ? styles.tabActive : ""}`}
              type="button"
              draggable
              onDragStart={(e) => handleTabDragStart(e, tab.path)}
              onClick={() => onSelectTab(pane.id, tab.path)}
              onAuxClick={(e) => {
                if (e.button !== 1) return;
                e.preventDefault();
                onCloseTabs(pane.id, [tab.path]);
              }}
              onContextMenu={(e) => {
                e.preventDefault();
                setTabMenu({ x: e.clientX, y: e.clientY, path: tab.path });
              }}
            >
              <span className={styles.tabName}>{tab.name}</span>
              {dirtyFiles.has(tab.path) && <span className={styles.tabDirty}>●</span>}
              <span
                className={styles.tabClose}
                onClick={(e) => {
                  e.stopPropagation();
                  onCloseTabs(pane.id, [tab.path]);
                }}
              >
                <X size={10} />
              </span>
            </button>
          ))}
          <div
            className={styles.tabFill}
            draggable
            onDragStart={handleTabbarDragStart}
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
      {activeConflict !== null && activeFile !== null && (
        <div className={styles.banner}>
          {activeConflict === "deleted" ? (
            <>
              <span className={styles.bannerText}>This file was deleted or moved on disk.</span>
              <button
                type="button"
                className={styles.bannerAction}
                onClick={() => onCloseTabs(pane.id, [activeFile])}
              >
                Close tab
              </button>
            </>
          ) : (
            <>
              <span className={styles.bannerText}>This file has changed on disk.</span>
              <button
                type="button"
                className={styles.bannerAction}
                onClick={() => void onReload(activeFile)}
              >
                Reload
              </button>
              <button
                type="button"
                className={styles.bannerActionMuted}
                onClick={() => onDismissConflict(activeFile)}
              >
                Keep mine
              </button>
            </>
          )}
        </div>
      )}
      <div className={styles.content}>
        {activeFile === null ? (
          <EmptyPrompt onOpenBrowser={(kind) => onConvertPane(pane.id, kind)} />
        ) : isLoading ? (
          <p className={styles.empty}>Loading…</p>
        ) : isBinary ? (
          <p className={styles.empty}>Binary file — cannot display</p>
        ) : activeContent !== undefined ? (
          <div
            className={styles.editorHost}
            onContextMenu={(e) => {
              if (!editor) return;
              e.preventDefault();
              setEditorMenu({ x: e.clientX, y: e.clientY });
            }}
          >
            <Editor
              path={activeFile}
              value={activeContent}
              language={getLanguage(activeFile)}
              theme="vs"
              loading=""
              onMount={handleMount}
              onChange={handleChange}
              options={{
                automaticLayout: true,
                minimap: { enabled: false },
                fontSize: 13,
                fontFamily: "'JetBrains Mono', ui-monospace, monospace",
                lineNumbers: "on",
                scrollBeyondLastLine: false,
                wordWrap: "on",
                renderLineHighlight: "gutter",
                guides: { indentation: true, bracketPairs: true },
                bracketPairColorization: { enabled: true },
                padding: { top: 8 },
                contextmenu: false,
              }}
            />
          </div>
        ) : (
          <p className={styles.empty}>Unable to read file</p>
        )}
        <DropOverlay
          active={dragActive}
          acceptedMimes={[TAB_DRAG_MIME, PANE_DRAG_MIME]}
          onDrop={(zone, e) => onPaneDrop(pane.id, zone, e)}
        />
      </div>
      {editorMenu && editor && (
        <ContextMenu
          x={editorMenu.x}
          y={editorMenu.y}
          items={buildEditorMenu(editor)}
          onClose={() => setEditorMenu(null)}
        />
      )}
      {tabMenu && (
        <ContextMenu
          x={tabMenu.x}
          y={tabMenu.y}
          items={buildTabMenu(tabMenu.path, pane.tabs, projectPath, (paths) =>
            onCloseTabs(pane.id, paths),
          )}
          onClose={() => setTabMenu(null)}
        />
      )}
    </div>
  );
}
