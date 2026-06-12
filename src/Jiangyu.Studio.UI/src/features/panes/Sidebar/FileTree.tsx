import { memo, use, useCallback, useEffect, useRef, useState } from "react";
import { File, Folder, FolderOpen } from "lucide-react";
import { rpcCall } from "@shared/rpc";
import { dirname, basename, join, isDescendant } from "@shared/path";
import { CROSS_TAB_MIME, encodeCrossTabPayload } from "@features/panes/crossTab";
import { attachDragChip } from "@shared/drag/chip";
import { ContextMenu } from "@shared/ui/ContextMenu/ContextMenu";
import {
  moveWithFeedback,
  performDelete,
  reportRpcError,
  validateName,
  type FileEntry,
} from "./fileOps";
import { buildEntryMenu } from "./sidebarMenus";
import { getFileIcon } from "./fileIcons";
import { SidebarVolatileContext, useSidebar, type SidebarContextValue } from "./sidebarContext";
import styles from "./Sidebar.module.css";

const DRAG_MIME = "application/x-jiangyu-path";

function setDragPath(e: React.DragEvent, path: string): void {
  e.dataTransfer.setData(DRAG_MIME, path);
  e.dataTransfer.effectAllowed = "move";
  attachDragChip(e, basename(path));
}

export interface ExpandRequest {
  id: number;
  paths: ReadonlySet<string>;
}

interface TreeNodeProps {
  path: string;
  depth: number;
  expandRequest: ExpandRequest | null;
}

export function TreeNode({ path, depth, expandRequest }: TreeNodeProps) {
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const { registerRefresh, pendingNew } = useSidebar();

  const fetchEntries = useCallback(() => {
    void (async () => {
      try {
        const result = await rpcCall<FileEntry[]>("listDirectory", { path });
        setEntries(result);
      } catch (err) {
        console.error("[FileTree] Failed to list:", path, err);
      }
    })();
  }, [path]);

  useEffect(() => fetchEntries(), [fetchEntries]);

  useEffect(() => {
    return registerRefresh(path, fetchEntries);
  }, [path, registerRefresh, fetchEntries]);

  const showPendingHere = pendingNew !== null && pendingNew.parentPath === path;

  return (
    <ul className={`${styles.nodeList}${depth > 0 ? ` ${styles.nodeListNested}` : ""}`}>
      {showPendingHere && <NewEntryRow />}
      {entries.map((entry) => (
        <li key={entry.path}>
          {entry.isDirectory ? (
            <DirectoryItem entry={entry} depth={depth} expandRequest={expandRequest} />
          ) : (
            <FileItem entry={entry} />
          )}
        </li>
      ))}
    </ul>
  );
}

interface DirectoryItemProps {
  entry: FileEntry;
  depth: number;
  expandRequest: ExpandRequest | null;
}

const DirectoryItem = memo(function DirectoryItem({
  entry,
  depth,
  expandRequest,
}: DirectoryItemProps) {
  const ctx = useSidebar();
  const [open, setOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [dropActive, setDropActive] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const isCut = ctx.clipboard?.mode === "cut" && ctx.clipboard.paths.includes(entry.path);

  // Consume each expand-request id at most once so the user can manually
  // close a folder we auto-opened without it springing back on next render.
  const requestId = expandRequest?.id ?? 0;
  const [lastSeenRequestId, setLastSeenRequestId] = useState(requestId);
  if (lastSeenRequestId !== requestId) {
    setLastSeenRequestId(requestId);
    if (expandRequest?.paths.has(entry.path) === true) {
      setOpen(true);
    }
  }

  const handleDragOver = (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(DRAG_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    setDropActive(true);
  };

  const handleDragLeave = () => setDropActive(false);

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDropActive(false);
    const srcPath = e.dataTransfer.getData(DRAG_MIME);
    if (!srcPath) return;
    if (isDescendant(srcPath, entry.path)) return;
    if (dirname(srcPath) === entry.path) return;
    const destPath = join(entry.path, basename(srcPath));
    void moveWithFeedback(srcPath, destPath, ctx);
  };

  return (
    <>
      <button
        className={`${styles.entry} ${entry.isIgnored ? styles.ignored : ""} ${dropActive ? styles.dropTarget : ""} ${isCut ? styles.cut : ""}`}
        type="button"
        draggable={!renaming}
        onClick={() => {
          if (!renaming) setOpen((prev) => !prev);
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setMenu({ x: e.clientX, y: e.clientY });
        }}
        onKeyDown={(e) => handleEntryKey(e, entry, ctx, () => setRenaming(true))}
        onDragStart={(e) => setDragPath(e, entry.path)}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <span className={styles.icon}>
          {open ? <FolderOpen size={12} /> : <Folder size={12} />}
        </span>
        {renaming ? (
          <RenameInput entry={entry} onDone={() => setRenaming(false)} />
        ) : (
          <span className={styles.entryName}>{entry.name}</span>
        )}
      </button>
      {open && <TreeNode path={entry.path} depth={depth + 1} expandRequest={expandRequest} />}
      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          items={buildEntryMenu(entry, { ...ctx, startRename: () => setRenaming(true) })}
          onClose={() => setMenu(null)}
        />
      )}
    </>
  );
});

// Thin wrapper around the memoised row: it alone subscribes to the volatile
// context and forwards per-row booleans, so a reveal or dirty flip re-renders
// every wrapper cheaply but only the affected rows.
function FileItem({ entry }: { entry: FileEntry }) {
  const { highlightedPath, dirtyPaths } = use(SidebarVolatileContext);
  return (
    <FileItemRow
      entry={entry}
      isHighlighted={highlightedPath === entry.path}
      isDirty={dirtyPaths.has(entry.path)}
    />
  );
}

interface FileItemRowProps {
  entry: FileEntry;
  isHighlighted: boolean;
  isDirty: boolean;
}

const FileItemRow = memo(function FileItemRow({ entry, isHighlighted, isDirty }: FileItemRowProps) {
  const ctx = useSidebar();
  const [renaming, setRenaming] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const isCut = ctx.clipboard?.mode === "cut" && ctx.clipboard.paths.includes(entry.path);
  const btnRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (isHighlighted && btnRef.current) {
      btnRef.current.scrollIntoView({ block: "nearest" });
    }
  }, [isHighlighted]);

  return (
    <>
      <button
        ref={btnRef}
        className={`${styles.entry} ${entry.isIgnored ? styles.ignored : ""} ${isCut ? styles.cut : ""} ${isHighlighted ? styles.highlighted : ""}`}
        type="button"
        draggable={!renaming}
        onClick={() => {
          if (!renaming) ctx.onOpenFile(entry.path);
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setMenu({ x: e.clientX, y: e.clientY });
        }}
        onKeyDown={(e) => handleEntryKey(e, entry, ctx, () => setRenaming(true))}
        onDragStart={(e) => {
          setDragPath(e, entry.path);
          // Also carry a cross-window tab payload so the entry can be
          // dropped onto any editor tab bar (primary or a secondary pane
          // window) to open the file there. No beginTabMove — the sidebar
          // isn't "moving" this file, just offering it.
          e.dataTransfer.setData(CROSS_TAB_MIME, encodeCrossTabPayload(entry.path));
        }}
      >
        <span className={styles.icon}>{getFileIcon(entry.name)}</span>
        {renaming ? (
          <RenameInput entry={entry} onDone={() => setRenaming(false)} />
        ) : (
          <>
            <span className={styles.entryName}>{entry.name}</span>
            {isDirty && <span className={styles.dirtyDot}>●</span>}
          </>
        )}
      </button>
      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          items={buildEntryMenu(entry, { ...ctx, startRename: () => setRenaming(true) })}
          onClose={() => setMenu(null)}
        />
      )}
    </>
  );
});

function RenameInput({ entry, onDone }: { entry: FileEntry; onDone: () => void }) {
  const { onPathMoved, invalidateDir, pushToast } = useSidebar();
  const [value, setValue] = useState(entry.name);
  const ref = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.focus();
    // Select up to (but not including) the extension, matching VS Code's rename UX
    const dot = entry.isDirectory ? -1 : entry.name.lastIndexOf(".");
    if (dot > 0) el.setSelectionRange(0, dot);
    else el.select();
  }, [entry.name, entry.isDirectory]);

  const commit = async () => {
    const result = validateName(value);
    if (result.error !== null) {
      pushToast({ variant: "error", message: result.error });
      return;
    }
    if (result.value === null || result.value === entry.name) {
      onDone();
      return;
    }
    const destPath = join(dirname(entry.path), result.value);
    try {
      await rpcCall<null>("movePath", { srcPath: entry.path, destPath });
      onPathMoved(entry.path, destPath);
      invalidateDir(dirname(entry.path));
      onDone();
    } catch (err) {
      reportRpcError(pushToast, "Rename", err);
    }
  };

  return (
    <input
      ref={ref}
      className={styles.renameInput}
      type="text"
      value={value}
      onChange={(e) => setValue(e.target.value)}
      onClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === "Enter") void commit();
        else if (e.key === "Escape") onDone();
      }}
      onBlur={() => void commit()}
    />
  );
}

function NewEntryRow() {
  const { pendingNew, setPendingNew, expandDirectory, invalidateDir, pushToast } = useSidebar();
  const [value, setValue] = useState("");
  const ref = useRef<HTMLInputElement>(null);

  useEffect(() => {
    ref.current?.focus();
  }, []);

  if (!pendingNew) return null;

  const commit = async () => {
    const result = validateName(value);
    if (result.error !== null) {
      pushToast({ variant: "error", message: result.error });
      return;
    }
    if (result.value === null) {
      setPendingNew(null);
      return;
    }
    const newPath = join(pendingNew.parentPath, result.value);
    const method = pendingNew.kind === "file" ? "createFile" : "createDirectory";
    try {
      await rpcCall<null>(method, { path: newPath });
      expandDirectory(pendingNew.parentPath);
      invalidateDir(pendingNew.parentPath);
      setPendingNew(null);
    } catch (err) {
      reportRpcError(pushToast, "Create", err);
    }
  };

  const Icon = pendingNew.kind === "folder" ? Folder : File;

  return (
    <li className={styles.newRow}>
      <span className={styles.icon}>
        <Icon size={12} />
      </span>
      <input
        ref={ref}
        className={styles.renameInput}
        type="text"
        value={value}
        placeholder={pendingNew.kind === "file" ? "new file" : "new folder"}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") void commit();
          else if (e.key === "Escape") setPendingNew(null);
        }}
        onBlur={() => void commit()}
      />
    </li>
  );
}

function handleEntryKey(
  e: React.KeyboardEvent,
  entry: FileEntry,
  ctx: SidebarContextValue,
  startRename: () => void,
) {
  if (e.key === "F2") {
    e.preventDefault();
    startRename();
  } else if (e.key === "Delete") {
    e.preventDefault();
    void performDelete(entry, ctx);
  }
}
