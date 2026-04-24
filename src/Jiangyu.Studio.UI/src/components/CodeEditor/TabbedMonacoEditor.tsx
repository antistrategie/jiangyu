import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { X } from "lucide-react";
import Editor, { type OnChange, type OnMount } from "@monaco-editor/react";
import type { editor as monacoEditor } from "monaco-editor";
import { ContextMenu, type ContextMenuEntry } from "@components/ContextMenu/ContextMenu.tsx";
import { buildJiangyuTheme } from "@components/CodeEditor/jiangyuTheme.ts";
import { registerKdlLanguage } from "@components/CodeEditor/kdlLanguage.ts";
import { getLanguage, isBinaryFile } from "@components/CodeEditor/fileTypes.ts";
import { computeTabDropIndex } from "@lib/drag/computeDropIndex.ts";
import { TemplateVisualEditor } from "@components/TemplateVisualEditor/TemplateVisualEditor.tsx";
import type { Tab } from "@lib/layout.ts";
import { rpcCall, type FileChangeKind } from "@lib/rpc.ts";
import {
  useEditorFontSize,
  useEditorKeybindMode,
  useEditorWordWrap,
  useTemplateEditorMode,
  type TemplateEditorMode,
} from "@lib/settings.ts";
import { useProjectStore } from "@lib/project/store.ts";
import styles from "@components/Workspace/Workspace.module.css";

export interface TabDragSlots {
  readonly onTabDragStart?: (e: React.DragEvent<HTMLButtonElement>, path: string) => void;
  readonly onTabbarDragOver?: (e: React.DragEvent<HTMLDivElement>) => void;
  readonly onTabbarDragLeave?: () => void;
  readonly onTabbarDrop?: (e: React.DragEvent<HTMLDivElement>) => void;
  readonly dropHover?: boolean;
}

export interface TabbedMonacoEditorProps {
  readonly tabs: readonly Tab[];
  readonly activePath: string | null;
  readonly activeContent: string | undefined;
  readonly activeConflict: FileChangeKind | null;
  readonly dirtyFiles: ReadonlySet<string>;

  readonly onSelectTab: (path: string) => void;
  readonly onCloseTab: (path: string) => void;
  readonly onActiveContentChange: (value: string) => void;
  readonly onSave: () => void | Promise<void>;
  readonly onReload: () => void | Promise<void>;
  readonly onDismissConflict: () => void;

  // Optional editor-lifecycle hooks (CodePane uses these to register the
  // editor instance per-pane and to claim focus when the user clicks in).
  readonly onEditorMount?: (editor: monacoEditor.IStandaloneCodeEditor) => void;
  readonly onEditorFocus?: () => void;

  // Toolbar slot: rendered at the right of the tab bar. CodePane fills this
  // with its split / tear-out / fullscreen / close pane buttons; pane windows
  // pass nothing.
  readonly toolbar?: ReactNode;

  // Pane-drag handle: CodePane makes the empty space after the last tab
  // drag-out the whole pane. Pane windows use the same affordance to drag
  // themselves back to the primary (cross-window pane drag).
  readonly onTabbarDragStart?: (e: React.DragEvent<HTMLDivElement>) => void;

  // In-window tab drag (e.g. cross-pane move). Callers opt in by supplying
  // drag handlers; without them the tabs aren't draggable.
  readonly tabDrag?: TabDragSlots;

  // Right-click menus. Both are optional so pane windows can skip them.
  readonly buildTabContextMenu?: (path: string) => ContextMenuEntry[];
  readonly buildEditorContextMenu?: (
    editor: monacoEditor.IStandaloneCodeEditor,
  ) => ContextMenuEntry[];

  // CodePane layers a DropOverlay on the content area for pane-drop zones.
  // Rendered after the editor so it composites over it.
  readonly dropOverlay?: ReactNode;

  // Replaces the whole content area when tabs is empty (e.g. the pane's
  // EmptyPrompt). When omitted, a plain "no file" placeholder is shown.
  readonly emptyState?: ReactNode;

  // Outer container customization. CodePane wires ref + flex + active-state
  // dimming; pane windows don't need any of this.
  readonly className?: string;
  readonly style?: React.CSSProperties;
  readonly containerRef?: (el: HTMLDivElement | null) => void;
  readonly onMouseDown?: () => void;
}

// Theme + KDL language register once per process. Flipping a module-level
// flag is fine: Monaco's defineTheme / registerLanguage are idempotent but
// we avoid the repeated work.
let globalsRegistered = false;

// monaco-vim patch applied once per process: wrap RegisterController.pushText
// so yank / delete / change operations also write their text into the OS
// clipboard. Without this, `y` / `yy` in vim mode only populates the internal
// register and nothing is available outside the editor.
let vimClipboardPatched = false;
type VimRegisterController = {
  pushText: (
    registerName: string,
    operator: string,
    text: string,
    linewise?: boolean,
    blockwise?: boolean,
  ) => void;
};
function patchVimClipboardBridge(vim: unknown): void {
  if (vimClipboardPatched) return;
  // monaco-vim's VimMode is the CMAdapter class with a static `Vim` holding
  // the core vim object. The shape isn't exported in a typed form, so we
  // access it structurally.
  const vimStatic = (vim as { Vim?: { getRegisterController?: () => VimRegisterController } }).Vim;
  const rc = vimStatic?.getRegisterController?.();
  if (rc === undefined) return;
  const original = rc.pushText.bind(rc);
  rc.pushText = (registerName, operator, text, linewise, blockwise) => {
    original(registerName, operator, text, linewise, blockwise);
    if (operator === "yank" || operator === "delete" || operator === "change") {
      if (typeof text === "string" && text.length > 0) {
        void navigator.clipboard.writeText(text).catch(() => {});
      }
    }
  };
  vimClipboardPatched = true;
}

export function TabbedMonacoEditor(props: TabbedMonacoEditorProps) {
  const {
    tabs,
    activePath,
    activeContent,
    activeConflict,
    dirtyFiles,
    onSelectTab,
    onCloseTab,
    onActiveContentChange,
    onSave,
    onReload,
    onDismissConflict,
    onEditorMount,
    onEditorFocus,
    toolbar,
    onTabbarDragStart,
    tabDrag,
    buildTabContextMenu,
    buildEditorContextMenu,
    dropOverlay,
    emptyState,
    className,
    style,
    containerRef,
    onMouseDown,
  } = props;

  const [editor, setEditor] = useState<monacoEditor.IStandaloneCodeEditor | null>(null);
  const monacoRef = useRef<typeof import("monaco-editor") | null>(null);
  const [editorMenu, setEditorMenu] = useState<{ x: number; y: number } | null>(null);
  const [tabMenu, setTabMenu] = useState<{ x: number; y: number; path: string } | null>(null);
  // Reorder indicator: which tab index the drop would land at. Only populated
  // while a tab from THIS bar is being dragged (tracked via isOwnDragRef) so
  // cross-pane drops — which currently always append — don't show a
  // misleading indicator.
  const [dropTargetIndex, setDropTargetIndex] = useState<number | null>(null);
  const isOwnDragRef = useRef(false);
  const [fontSize] = useEditorFontSize();
  const [wordWrap] = useEditorWordWrap();
  const [keybindMode] = useEditorKeybindMode();
  const [defaultEditorMode] = useTemplateEditorMode();
  const projectPath = useProjectStore((s) => s.projectPath);
  // Per-file mode overrides: paths in this set deviate from the global default.
  const [modeOverrides, setModeOverrides] = useState<Set<string>>(new Set());
  const vimStatusRef = useRef<HTMLDivElement>(null);
  const activeTabRef = useRef<HTMLButtonElement>(null);

  const isTemplateKdl =
    activePath !== null && activePath.includes("/templates/") && activePath.endsWith(".kdl");

  const activeMode: TemplateEditorMode =
    isTemplateKdl && activePath !== null
      ? modeOverrides.has(activePath)
        ? defaultEditorMode === "visual"
          ? "source"
          : "visual"
        : defaultEditorMode
      : "source";

  const setMode = useCallback(
    (target: TemplateEditorMode) => {
      if (activePath === null) return;
      const shouldOverride = target !== defaultEditorMode;
      setModeOverrides((prev) => {
        const has = prev.has(activePath);
        if (has === shouldOverride) return prev;
        const next = new Set(prev);
        if (shouldOverride) next.add(activePath);
        else next.delete(activePath);
        return next;
      });
    },
    [activePath, defaultEditorMode],
  );

  const forceSourceMode = useCallback(() => setMode("source"), [setMode]);

  // Monaco's onMount captures the initial onSave in its Ctrl+S handler, so
  // route through a ref that always points at the latest prop.
  const saveRef = useRef(onSave);
  useEffect(() => {
    saveRef.current = onSave;
  }, [onSave]);

  // Scroll the active tab into view when the active file changes.
  useEffect(() => {
    activeTabRef.current?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [activePath]);

  const handleMount: OnMount = useCallback(
    (mountedEditor, monaco) => {
      monacoRef.current = monaco;
      if (!globalsRegistered) {
        monaco.editor.defineTheme("jiangyu", buildJiangyuTheme());
        registerKdlLanguage(monaco);
        globalsRegistered = true;
      }
      monaco.editor.setTheme("jiangyu");
      mountedEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        void saveRef.current();
      });
      if (onEditorFocus !== undefined) {
        mountedEditor.onDidFocusEditorWidget(onEditorFocus);
      }
      mountedEditor.onDidDispose(() => setEditor(null));
      setEditor(mountedEditor);
      onEditorMount?.(mountedEditor);
    },
    [onEditorFocus, onEditorMount],
  );

  const handleChange: OnChange = useCallback(
    (value) => {
      if (value === undefined) return;
      onActiveContentChange(value);
    },
    [onActiveContentChange],
  );

  // monaco-vim: attach keybindings + optional status bar. Lazy-imported so
  // the ~40KB bundle only loads when vim mode is enabled. :w / :write / :x
  // route through saveRef so they always save the active tab. We also wrap
  // the RegisterController so yank/delete/change operations mirror their
  // text into the system clipboard — monaco-vim's default register model
  // doesn't touch navigator.clipboard.
  useEffect(() => {
    if (editor === null || keybindMode !== "vim") return;
    let adapter: { dispose: () => void } | null = null;
    let cancelled = false;
    void import("monaco-vim").then(({ initVimMode, VimMode }) => {
      if (cancelled) return;
      const commands = VimMode.commands as Record<string, unknown>;
      commands.save = () => {
        void saveRef.current();
      };
      patchVimClipboardBridge(VimMode);
      adapter = initVimMode(editor, vimStatusRef.current);
    });
    return () => {
      cancelled = true;
      adapter?.dispose();
    };
  }, [editor, keybindMode]);

  // KDL parse diagnostics: run templatesParse on template KDL files and set
  // Monaco model markers so errors appear as squiggles in source mode.
  const markerTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => {
    const monaco = monacoRef.current;
    const model = editor?.getModel();
    if (!isTemplateKdl || !monaco || !model) {
      // Clear markers when switching away from a template file
      if (monaco && model) monaco.editor.setModelMarkers(model, "jiangyu-kdl", []);
      return;
    }
    const text = activeContent ?? "";
    clearTimeout(markerTimer.current);
    markerTimer.current = setTimeout(() => {
      void rpcCall<{ errors: { message: string; line?: number }[] }>("templatesParse", { text })
        .then((doc) => {
          // Only set if model is still current
          if (editor?.getModel() !== model) return;
          const markers: monacoEditor.IMarkerData[] = doc.errors.map((err) => ({
            severity: monaco.MarkerSeverity.Error,
            message: err.message,
            startLineNumber: err.line ?? 1,
            startColumn: 1,
            endLineNumber: err.line ?? 1,
            endColumn: 1000,
          }));
          monaco.editor.setModelMarkers(model, "jiangyu-kdl", markers);
        })
        .catch(() => {
          // RPC failure — clear markers rather than show stale data
          if (editor?.getModel() === model) {
            monaco.editor.setModelMarkers(model, "jiangyu-kdl", []);
          }
        });
    }, 300);
    return () => clearTimeout(markerTimer.current);
  }, [editor, isTemplateKdl, activeContent]);

  // Font family and padding come from design tokens (no user-visible control).
  // Font size and word wrap come from settings so they live on re-renders, and
  // @monaco-editor/react forwards options changes into editor.updateOptions.
  const editorOptions = useMemo<monacoEditor.IStandaloneEditorConstructionOptions>(() => {
    const root = getComputedStyle(document.documentElement);
    const fontFamily =
      root.getPropertyValue("--font-mono").trim() || "'JetBrains Mono', ui-monospace, monospace";
    const padding = parseInt(root.getPropertyValue("--space-2").trim(), 10) || 8;
    return {
      automaticLayout: true,
      minimap: { enabled: false },
      fontSize,
      fontFamily,
      lineNumbers: "on",
      scrollBeyondLastLine: false,
      wordWrap,
      renderLineHighlight: "gutter",
      guides: { indentation: true, bracketPairs: true },
      bracketPairColorization: { enabled: true },
      padding: { top: padding },
      contextmenu: false,
    };
  }, [fontSize, wordWrap]);

  const isBinary = activePath !== null && isBinaryFile(activePath);
  const isLoading = activePath !== null && !isBinary && activeContent === undefined;
  const showEditor = activePath !== null && !isBinary && activeContent !== undefined;

  return (
    <div
      ref={containerRef}
      className={`${styles.editor} ${className ?? ""}`.trim()}
      style={style}
      onMouseDown={onMouseDown}
    >
      <div className={`${styles.tabbar} ${tabDrag?.dropHover ? styles.tabbarDrop : ""}`}>
        <div
          className={styles.tabScroll}
          onWheel={(e) => {
            if (e.deltaY === 0) return;
            e.currentTarget.scrollLeft += e.deltaY;
          }}
          onDragOver={(e) => {
            if (isOwnDragRef.current) {
              setDropTargetIndex(computeTabDropIndex(e.currentTarget, e.clientX));
            }
            tabDrag?.onTabbarDragOver?.(e);
          }}
          onDragLeave={() => {
            setDropTargetIndex(null);
            tabDrag?.onTabbarDragLeave?.();
          }}
          onDrop={(e) => {
            setDropTargetIndex(null);
            tabDrag?.onTabbarDrop?.(e);
          }}
        >
          {tabs.map((tab, i) => (
            <button
              key={tab.path}
              ref={tab.path === activePath ? activeTabRef : undefined}
              data-tab-path={tab.path}
              className={[
                styles.tab,
                tab.path === activePath ? styles.tabActive : "",
                dropTargetIndex === i ? styles.tabDropBefore : "",
                dropTargetIndex === tabs.length && i === tabs.length - 1 ? styles.tabDropAfter : "",
              ]
                .filter(Boolean)
                .join(" ")}
              type="button"
              draggable={tabDrag?.onTabDragStart !== undefined}
              onDragStart={(e) => {
                isOwnDragRef.current = true;
                tabDrag?.onTabDragStart?.(e, tab.path);
              }}
              onDragEnd={() => {
                isOwnDragRef.current = false;
                setDropTargetIndex(null);
              }}
              onClick={() => onSelectTab(tab.path)}
              onAuxClick={(e) => {
                if (e.button !== 1) return;
                e.preventDefault();
                onCloseTab(tab.path);
              }}
              onContextMenu={
                buildTabContextMenu !== undefined
                  ? (e) => {
                      e.preventDefault();
                      setTabMenu({ x: e.clientX, y: e.clientY, path: tab.path });
                    }
                  : undefined
              }
              title={tab.path}
            >
              <span className={styles.tabName}>{tab.name}</span>
              {dirtyFiles.has(tab.path) && <span className={styles.tabDirty}>●</span>}
              <span
                className={styles.tabClose}
                onClick={(e) => {
                  e.stopPropagation();
                  onCloseTab(tab.path);
                }}
              >
                <X size={10} />
              </span>
            </button>
          ))}
          <div
            className={styles.tabFill}
            draggable={onTabbarDragStart !== undefined}
            onDragStart={onTabbarDragStart}
            title={onTabbarDragStart !== undefined ? "Drag to move pane" : undefined}
          />
        </div>
        {toolbar !== undefined && <div className={styles.tabActions}>{toolbar}</div>}
      </div>
      {activePath !== null && activeConflict !== null && (
        <div className={styles.banner}>
          {activeConflict === "deleted" ? (
            <>
              <span className={styles.bannerText}>This file was deleted or moved on disk.</span>
              <button
                type="button"
                className={styles.bannerAction}
                onClick={() => onCloseTab(activePath)}
              >
                Close tab
              </button>
            </>
          ) : (
            <>
              <span className={styles.bannerText}>This file has changed on disk.</span>
              <button type="button" className={styles.bannerAction} onClick={() => void onReload()}>
                Reload
              </button>
              <button
                type="button"
                className={styles.bannerActionMuted}
                onClick={onDismissConflict}
              >
                Keep mine
              </button>
            </>
          )}
        </div>
      )}
      {activePath !== null && (
        <div className={styles.editorBar}>
          <span className={styles.editorBarPath}>
            {projectPath && activePath.startsWith(projectPath)
              ? activePath.slice(projectPath.length + 1)
              : activePath}
          </span>
          {isTemplateKdl && (
            <span className={styles.editorBarActions}>
              <button
                type="button"
                className={`${styles.modeBtn} ${activeMode === "visual" ? styles.modeBtnActive : ""}`}
                onClick={() => setMode("visual")}
              >
                Visual
              </button>
              <button
                type="button"
                className={`${styles.modeBtn} ${activeMode === "source" ? styles.modeBtnActive : ""}`}
                onClick={() => setMode("source")}
              >
                Source
              </button>
            </span>
          )}
        </div>
      )}
      <div className={styles.content}>
        {activePath === null ? (
          (emptyState ?? <p className={styles.empty}>No file open</p>)
        ) : isLoading ? (
          <p className={styles.empty}>Loading…</p>
        ) : isBinary ? (
          <p className={styles.empty}>Binary file — cannot display</p>
        ) : activeMode === "visual" ? (
          <TemplateVisualEditor
            content={activeContent ?? ""}
            filePath={activePath}
            onChange={onActiveContentChange}
            onRequestSourceMode={forceSourceMode}
          />
        ) : showEditor ? (
          <div
            className={styles.editorHost}
            // Capture-phase drop handlers so tab-bar drops and cross-window
            // file drops are routed through the same consumer callback even
            // when released over the editor surface. Without these, Monaco
            // pastes the raw drag payload as text.
            onDragOverCapture={tabDrag?.onTabbarDragOver}
            onDragLeaveCapture={tabDrag?.onTabbarDragLeave}
            onDropCapture={tabDrag?.onTabbarDrop}
            onContextMenu={
              buildEditorContextMenu !== undefined
                ? (e) => {
                    if (editor === null) return;
                    e.preventDefault();
                    setEditorMenu({ x: e.clientX, y: e.clientY });
                  }
                : undefined
            }
          >
            <div className={styles.editorInner}>
              <Editor
                path={activePath}
                value={activeContent}
                language={getLanguage(activePath)}
                theme="jiangyu"
                loading=""
                onMount={handleMount}
                onChange={handleChange}
                options={editorOptions}
              />
            </div>
            {keybindMode === "vim" && <div ref={vimStatusRef} className={styles.vimStatus} />}
          </div>
        ) : (
          <p className={styles.empty}>Unable to read file</p>
        )}
        {dropOverlay}
      </div>
      {editorMenu !== null && editor !== null && buildEditorContextMenu !== undefined && (
        <ContextMenu
          x={editorMenu.x}
          y={editorMenu.y}
          items={buildEditorContextMenu(editor)}
          onClose={() => setEditorMenu(null)}
        />
      )}
      {tabMenu !== null && buildTabContextMenu !== undefined && (
        <ContextMenu
          x={tabMenu.x}
          y={tabMenu.y}
          items={buildTabContextMenu(tabMenu.path)}
          onClose={() => setTabMenu(null)}
        />
      )}
    </div>
  );
}
