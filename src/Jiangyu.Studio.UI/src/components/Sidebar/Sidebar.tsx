import { useState, useEffect, useCallback, useRef, useContext, createContext } from "react";
import {
  Folder,
  FolderOpen,
  File,
  FileText,
  FileCode,
  FileImage,
  FileAudio,
  Box,
} from "lucide-react";
import { rpcCall, subscribe, type FileChangedEvent } from "../../lib/rpc.ts";
import { dirname } from "../../lib/path.ts";
import styles from "./Sidebar.module.css";

interface FileEntry {
  readonly name: string;
  readonly path: string;
  readonly isDirectory: boolean;
  readonly isIgnored?: boolean;
}

// Single fileChanged subscription per Sidebar, with TreeNodes registering
// invalidation callbacks by directory path. Avoids O(nodes) listener scans
// per event when many directories are expanded.
type InvalidateCallback = () => void;
interface TreeRefreshContextValue {
  register: (path: string, cb: InvalidateCallback) => () => void;
}
const TreeRefreshContext = createContext<TreeRefreshContextValue | null>(null);

interface SidebarProps {
  projectPath: string;
  onOpenFile: (path: string) => void;
  dirtyPaths: Set<string>;
}

export function Sidebar({ projectPath, onOpenFile, dirtyPaths }: SidebarProps) {
  const [width, setWidth] = useState(240);
  const dragging = useRef(false);
  const refreshers = useRef<Map<string, Set<InvalidateCallback>>>(new Map());

  const register = useCallback((path: string, cb: InvalidateCallback) => {
    let set = refreshers.current.get(path);
    if (!set) {
      set = new Set();
      refreshers.current.set(path, set);
    }
    set.add(cb);
    return () => {
      const s = refreshers.current.get(path);
      if (!s) return;
      s.delete(cb);
      if (s.size === 0) refreshers.current.delete(path);
    };
  }, []);

  useEffect(() => {
    return subscribe<FileChangedEvent>("fileChanged", (event) => {
      const set = refreshers.current.get(dirname(event.path));
      if (!set) return;
      for (const cb of set) cb();
    });
  }, []);

  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    dragging.current = true;

    const onMouseMove = (ev: MouseEvent) => {
      if (dragging.current) {
        setWidth(Math.max(160, Math.min(ev.clientX, 600)));
      }
    };
    const onMouseUp = () => {
      dragging.current = false;
      document.removeEventListener("mousemove", onMouseMove);
      document.removeEventListener("mouseup", onMouseUp);
    };

    document.addEventListener("mousemove", onMouseMove);
    document.addEventListener("mouseup", onMouseUp);
  }, []);

  return (
    <TreeRefreshContext.Provider value={{ register }}>
      <aside className={styles.sidebar} style={{ width, minWidth: 160, maxWidth: 600 }}>
        <div className={styles.tree}>
          <TreeNode
            path={projectPath}
            depth={0}
            onOpenFile={onOpenFile}
            dirtyPaths={dirtyPaths}
            defaultOpen
          />
        </div>
        <div className={styles.resizeHandle} onMouseDown={handleMouseDown} />
      </aside>
    </TreeRefreshContext.Provider>
  );
}

interface TreeNodeProps {
  path: string;
  depth: number;
  onOpenFile: (path: string) => void;
  dirtyPaths: Set<string>;
  defaultOpen?: boolean;
}

function TreeNode({ path, depth, onOpenFile, dirtyPaths }: TreeNodeProps) {
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [loaded, setLoaded] = useState(false);
  const refresh = useContext(TreeRefreshContext);

  useEffect(() => {
    if (!loaded) {
      void (async () => {
        try {
          const result = await rpcCall<FileEntry[]>("listDirectory", { path });
          setEntries(result);
          setLoaded(true);
        } catch (err) {
          console.error("[FileTree] Failed to list:", path, err);
        }
      })();
    }
  }, [path, loaded]);

  useEffect(() => {
    if (!refresh) return;
    return refresh.register(path, () => {
      setLoaded(false);
    });
  }, [path, refresh]);

  return (
    <ul
      className={styles.nodeList}
      style={
        depth > 0
          ? { marginLeft: 12, borderLeft: "1px solid var(--rule-faint)", paddingLeft: 0 }
          : undefined
      }
    >
      {entries.map((entry) => (
        <li key={entry.path}>
          {entry.isDirectory ? (
            <DirectoryItem
              entry={entry}
              depth={depth}
              onOpenFile={onOpenFile}
              dirtyPaths={dirtyPaths}
            />
          ) : (
            <FileItem entry={entry} onOpenFile={onOpenFile} isDirty={dirtyPaths.has(entry.path)} />
          )}
        </li>
      ))}
    </ul>
  );
}

function DirectoryItem({
  entry,
  depth,
  onOpenFile,
  dirtyPaths,
}: {
  entry: FileEntry;
  depth: number;
  onOpenFile: (path: string) => void;
  dirtyPaths: Set<string>;
}) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <button
        className={`${styles.entry} ${entry.isIgnored ? styles.ignored : ""}`}
        type="button"
        onClick={() => setOpen((prev) => !prev)}
      >
        <span className={styles.icon}>
          {open ? <FolderOpen size={12} /> : <Folder size={12} />}
        </span>
        <span className={styles.entryName}>{entry.name}</span>
      </button>
      {open && (
        <TreeNode
          path={entry.path}
          depth={depth + 1}
          onOpenFile={onOpenFile}
          dirtyPaths={dirtyPaths}
        />
      )}
    </>
  );
}

function getFileIcon(name: string) {
  const ext = name.split(".").pop()?.toLowerCase();
  switch (ext) {
    case "kdl":
    case "json":
    case "ts":
    case "tsx":
    case "cs":
    case "xml":
    case "toml":
      return <FileCode size={12} />;
    case "png":
    case "jpg":
    case "jpeg":
    case "webp":
    case "tga":
    case "bmp":
      return <FileImage size={12} />;
    case "wav":
    case "ogg":
    case "mp3":
    case "flac":
      return <FileAudio size={12} />;
    case "gltf":
    case "glb":
    case "fbx":
      return <Box size={12} />;
    case "md":
    case "txt":
      return <FileText size={12} />;
    default:
      return <File size={12} />;
  }
}

function FileItem({
  entry,
  onOpenFile,
  isDirty,
}: {
  entry: FileEntry;
  onOpenFile: (path: string) => void;
  isDirty: boolean;
}) {
  return (
    <button
      className={`${styles.entry} ${entry.isIgnored ? styles.ignored : ""}`}
      type="button"
      onClick={() => {
        onOpenFile(entry.path);
      }}
    >
      <span className={styles.icon}>{getFileIcon(entry.name)}</span>
      <span className={styles.entryName}>{entry.name}</span>
      {isDirty && <span className={styles.dirtyDot}>●</span>}
    </button>
  );
}
