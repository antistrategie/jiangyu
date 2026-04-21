import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import { X } from "lucide-react";
import Editor, { type OnMount, type OnChange } from "@monaco-editor/react";
import type { editor as monacoEditor } from "monaco-editor";
import { jiangyuTheme } from "./jiangyuTheme.ts";
import { rpcCall, subscribe, type FileChangedEvent, type FileChangeKind } from "../../lib/rpc.ts";
import { ContextMenu, type ContextMenuEntry } from "../ContextMenu/ContextMenu.tsx";
import type { OpenFile } from "../../App.tsx";
import { buildTabMenu } from "./tabMenu.ts";
import { fileTargetCommands } from "../../lib/fileCommands.ts";
import { PALETTE_SCOPE, useRegisterActions, type PaletteAction } from "../../lib/actions.tsx";
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

type ConflictKind = FileChangeKind;

interface EditorAreaProps {
  projectPath: string;
  openFiles: OpenFile[];
  activeFile: string | null;
  dirtyFiles: Set<string>;
  onSelectFile: (path: string) => void;
  onCloseFiles: (paths: string[]) => void;
  onMarkDirty: (path: string, isDirty: boolean) => void;
}

/** Map file extension to Monaco language ID. Monaco supports most common languages natively. */
function getLanguage(path: string): string {
  const ext = path.split(".").pop()?.toLowerCase() ?? "";
  switch (ext) {
    case "ts":
    case "tsx":
      return "typescript";
    case "js":
    case "jsx":
    case "mjs":
    case "cjs":
      return "javascript";
    case "json":
    case "jsonc":
      return "json";
    case "cs":
      return "csharp";
    case "xml":
    case "csproj":
    case "props":
    case "targets":
    case "slnx":
    case "svg":
      return "xml";
    case "css":
      return "css";
    case "scss":
      return "scss";
    case "less":
      return "less";
    case "html":
    case "htm":
      return "html";
    case "md":
    case "markdown":
      return "markdown";
    case "toml":
      return "ini";
    case "yaml":
    case "yml":
      return "yaml";
    case "py":
      return "python";
    case "rs":
      return "rust";
    case "go":
      return "go";
    case "java":
      return "java";
    case "kt":
    case "kts":
      return "kotlin";
    case "swift":
      return "swift";
    case "rb":
      return "ruby";
    case "php":
      return "php";
    case "sh":
    case "bash":
    case "zsh":
      return "shell";
    case "ps1":
      return "powershell";
    case "bat":
    case "cmd":
      return "bat";
    case "sql":
      return "sql";
    case "graphql":
    case "gql":
      return "graphql";
    case "dockerfile":
      return "dockerfile";
    case "lua":
      return "lua";
    case "r":
      return "r";
    case "cpp":
    case "cc":
    case "cxx":
    case "hpp":
    case "hxx":
      return "cpp";
    case "c":
    case "h":
      return "c";
    case "m":
    case "mm":
      return "objective-c";
    case "fs":
    case "fsx":
    case "fsi":
      return "fsharp";
    case "vb":
      return "vb";
    case "ini":
    case "cfg":
    case "conf":
      return "ini";
    case "diff":
    case "patch":
      return "plaintext";
    case "kdl":
      return "plaintext";
    default:
      return "plaintext";
  }
}

const BINARY_EXTENSIONS = new Set([
  "glb",
  "bin",
  "png",
  "jpg",
  "jpeg",
  "gif",
  "bmp",
  "ico",
  "webp",
  "tga",
  "dds",
  "psd",
  "tif",
  "tiff",
  "exr",
  "hdr",
  "wav",
  "mp3",
  "ogg",
  "flac",
  "aac",
  "wma",
  "mp4",
  "avi",
  "mkv",
  "mov",
  "webm",
  "zip",
  "tar",
  "gz",
  "bz2",
  "7z",
  "rar",
  "exe",
  "dll",
  "so",
  "dylib",
  "o",
  "obj",
  "woff",
  "woff2",
  "ttf",
  "otf",
  "eot",
  "bundle",
  "assets",
  "resource",
  "resS",
  "pdf",
  "doc",
  "docx",
  "xls",
  "xlsx",
]);

function isBinaryFile(path: string): boolean {
  const ext = path.split(".").pop()?.toLowerCase() ?? "";
  return BINARY_EXTENSIONS.has(ext);
}

export function EditorArea({
  projectPath,
  openFiles,
  activeFile,
  dirtyFiles,
  onSelectFile,
  onCloseFiles,
  onMarkDirty,
}: EditorAreaProps) {
  const [contents, setContents] = useState<Map<string, string>>(new Map());
  const [conflicts, setConflicts] = useState<Map<string, ConflictKind>>(new Map());
  const [loading, setLoading] = useState(false);
  const [binary, setBinary] = useState(false);
  const [editorMenu, setEditorMenu] = useState<{ x: number; y: number } | null>(null);
  const [tabMenu, setTabMenu] = useState<{ x: number; y: number; path: string } | null>(null);
  const [editor, setEditor] = useState<monacoEditor.IStandaloneCodeEditor | null>(null);
  const themeRegistered = useRef(false);

  const activeFileRef = useRef(activeFile);
  const contentsRef = useRef(contents);
  const openPathsRef = useRef<Set<string>>(new Set());
  const dirtyFilesRef = useRef(dirtyFiles);
  const onMarkDirtyRef = useRef(onMarkDirty);
  useEffect(() => {
    activeFileRef.current = activeFile;
  }, [activeFile]);
  useEffect(() => {
    contentsRef.current = contents;
  }, [contents]);
  useEffect(() => {
    dirtyFilesRef.current = dirtyFiles;
  }, [dirtyFiles]);
  useEffect(() => {
    onMarkDirtyRef.current = onMarkDirty;
  }, [onMarkDirty]);

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

  const handleSave = useCallback(async () => {
    const path = activeFileRef.current;
    if (path === null) return;
    const content = contentsRef.current.get(path);
    if (content === undefined) return;
    try {
      await rpcCall<null>("writeFile", { path, content });
      onMarkDirtyRef.current(path, false);
      clearConflict(path);
    } catch (err) {
      console.error("[Editor] writeFile failed:", err);
    }
  }, [clearConflict]);

  const handleReload = useCallback(async () => {
    const path = activeFileRef.current;
    if (path === null) return;
    try {
      const text = await rpcCall<string>("readFile", { path });
      setContent(path, text);
      onMarkDirtyRef.current(path, false);
      clearConflict(path);
    } catch (err) {
      console.error("[Editor] reload failed:", err);
    }
  }, [clearConflict, setContent]);

  const handleDismissConflict = useCallback(() => {
    const path = activeFileRef.current;
    if (path === null) return;
    clearConflict(path);
  }, [clearConflict]);

  const fileActions = useMemo<PaletteAction[]>(() => {
    if (activeFile === null) return [];
    const commands = fileTargetCommands(activeFile, projectPath, onCloseFiles);
    const close = commands.find((c) => c.id === "close")!;
    const extras = commands.filter((c) => c.id !== "close");

    const result: PaletteAction[] = [
      {
        id: "editor.save",
        label: "Save",
        scope: PALETTE_SCOPE.File,
        cn: "保存",
        kbd: "Ctrl+S",
        run: () => void handleSave(),
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
  }, [activeFile, projectPath, handleSave, onCloseFiles]);

  const monacoActions = useMemo<PaletteAction[]>(() => {
    if (!editor) return [];
    // Monaco doesn't expose a public way to read an action's keybinding; the
    // standalone editor keeps it on the internal _standaloneKeybindingService.
    // Accessing it lets us show shortcuts next to each command like VS Code.
    const kbService = (
      editor as unknown as {
        _standaloneKeybindingService?: {
          lookupKeybinding: (id: string) => { getLabel: () => string | null } | undefined;
        };
      }
    )._standaloneKeybindingService;

    return editor.getSupportedActions().map((action) => {
      const kbd = kbService?.lookupKeybinding(action.id)?.getLabel() ?? undefined;
      return {
        id: `monaco.${action.id}`,
        label: action.label,
        scope: PALETTE_SCOPE.Editor,
        ...(kbd !== undefined && kbd.length > 0 ? { kbd } : {}),
        run: () => {
          editor.focus();
          void action.run();
        },
      };
    });
  }, [editor]);

  useRegisterActions(fileActions);
  useRegisterActions(monacoActions);

  const handleMount: OnMount = useCallback(
    (editor, monaco) => {
      setEditor(editor);
      editor.onDidDispose(() => setEditor(null));
      if (!themeRegistered.current) {
        monaco.editor.defineTheme("jiangyu", jiangyuTheme);
        themeRegistered.current = true;
      }
      monaco.editor.setTheme("jiangyu");
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        void handleSave();
      });
    },
    [handleSave],
  );

  const handleChange: OnChange = useCallback(
    (value) => {
      const path = activeFileRef.current;
      if (path === null || value === undefined) return;
      setContent(path, value);
      onMarkDirtyRef.current(path, true);
    },
    [setContent],
  );

  useEffect(() => {
    if (!activeFile) {
      setBinary(false);
      return;
    }
    if (isBinaryFile(activeFile)) {
      setBinary(true);
      setLoading(false);
      return;
    }
    setBinary(false);
    if (contentsRef.current.has(activeFile)) {
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    void rpcCall<string>("readFile", { path: activeFile })
      .then((text) => {
        if (cancelled) return;
        setContent(activeFile, text);
      })
      .catch((err) => {
        console.error("[Editor] readFile failed:", err);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeFile, setContent]);

  useEffect(() => {
    const openPaths = new Set(openFiles.map((f) => f.path));
    openPathsRef.current = openPaths;
    const pruneClosed = <V,>(prev: Map<string, V>): Map<string, V> => {
      let changed = false;
      const next = new Map(prev);
      for (const key of next.keys()) {
        if (!openPaths.has(key)) {
          next.delete(key);
          changed = true;
        }
      }
      return changed ? next : prev;
    };
    setContents(pruneClosed);
    setConflicts(pruneClosed);
  }, [openFiles]);

  useEffect(() => {
    return subscribe<FileChangedEvent>("fileChanged", (event) => {
      if (!openPathsRef.current.has(event.path)) return;

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
  }, [setConflict, setContent]);

  const activeContent = activeFile !== null ? contents.get(activeFile) : undefined;
  const activeConflict = activeFile !== null ? (conflicts.get(activeFile) ?? null) : null;

  if (openFiles.length === 0) {
    return (
      <div className={styles.editor}>
        <div className={styles.content}>
          <p className={styles.empty}>Open a file from the sidebar to begin editing</p>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.editor}>
      <div
        className={styles.tabbar}
        onWheel={(e) => {
          if (e.deltaY === 0) return;
          e.currentTarget.scrollLeft += e.deltaY;
        }}
      >
        {openFiles.map((f) => (
          <button
            key={f.path}
            className={`${styles.tab} ${f.path === activeFile ? styles.tabActive : ""}`}
            type="button"
            onClick={() => {
              onSelectFile(f.path);
            }}
            onAuxClick={(e) => {
              if (e.button !== 1) return;
              e.preventDefault();
              onCloseFiles([f.path]);
            }}
            onContextMenu={(e) => {
              e.preventDefault();
              setTabMenu({ x: e.clientX, y: e.clientY, path: f.path });
            }}
          >
            <span className={styles.tabName}>{f.name}</span>
            {dirtyFiles.has(f.path) && <span className={styles.tabDirty}>●</span>}
            <span
              className={styles.tabClose}
              onClick={(e) => {
                e.stopPropagation();
                onCloseFiles([f.path]);
              }}
            >
              <X size={10} />
            </span>
          </button>
        ))}
        <div className={styles.tabFill} />
      </div>
      {activeConflict !== null && activeFile !== null && (
        <div className={styles.banner}>
          {activeConflict === "deleted" ? (
            <>
              <span className={styles.bannerText}>This file was deleted or moved on disk.</span>
              <button
                type="button"
                className={styles.bannerAction}
                onClick={() => {
                  onCloseFiles([activeFile]);
                }}
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
                onClick={() => {
                  void handleReload();
                }}
              >
                Reload
              </button>
              <button
                type="button"
                className={styles.bannerActionMuted}
                onClick={handleDismissConflict}
              >
                Keep mine
              </button>
            </>
          )}
        </div>
      )}
      <div className={styles.content}>
        {loading ? (
          <p className={styles.empty}>Loading…</p>
        ) : binary ? (
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
              path={activeFile ?? "untitled"}
              value={activeContent}
              language={getLanguage(activeFile ?? "")}
              theme="vs"
              loading=""
              onMount={handleMount}
              onChange={handleChange}
              options={{
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
          items={buildTabMenu(tabMenu.path, openFiles, projectPath, onCloseFiles)}
          onClose={() => setTabMenu(null)}
        />
      )}
    </div>
  );
}
