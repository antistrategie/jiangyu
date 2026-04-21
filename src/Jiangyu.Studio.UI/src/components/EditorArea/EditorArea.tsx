import { useState, useEffect, useRef, useCallback } from "react";
import { X } from "lucide-react";
import Editor, { type OnMount, type OnChange } from "@monaco-editor/react";
import { jiangyuTheme } from "./jiangyuTheme.ts";
import { rpcCall, subscribe, type FileChangedEvent, type FileChangeKind } from "../../lib/rpc.ts";
import type { OpenFile } from "../../App.tsx";
import styles from "./EditorArea.module.css";

type ConflictKind = FileChangeKind;

interface EditorAreaProps {
  openFiles: OpenFile[];
  activeFile: string | null;
  dirtyFiles: Set<string>;
  onSelectFile: (path: string) => void;
  onCloseFile: (path: string) => void;
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
  openFiles,
  activeFile,
  dirtyFiles,
  onSelectFile,
  onCloseFile,
  onMarkDirty,
}: EditorAreaProps) {
  const [contents, setContents] = useState<Map<string, string>>(new Map());
  const [conflicts, setConflicts] = useState<Map<string, ConflictKind>>(new Map());
  const [loading, setLoading] = useState(false);
  const [binary, setBinary] = useState(false);
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

  const handleMount: OnMount = useCallback(
    (editor, monaco) => {
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
      <div className={styles.tabbar}>
        {openFiles.map((f) => (
          <button
            key={f.path}
            className={`${styles.tab} ${f.path === activeFile ? styles.tabActive : ""}`}
            type="button"
            onClick={() => {
              onSelectFile(f.path);
            }}
          >
            <span className={styles.tabName}>{f.name}</span>
            {dirtyFiles.has(f.path) && <span className={styles.tabDirty}>●</span>}
            <span
              className={styles.tabClose}
              onClick={(e) => {
                e.stopPropagation();
                onCloseFile(f.path);
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
                  onCloseFile(activeFile);
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
            }}
          />
        ) : (
          <p className={styles.empty}>Unable to read file</p>
        )}
      </div>
    </div>
  );
}
