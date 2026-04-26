import { useState, useEffect, useCallback, useRef, use, createContext, useMemo } from "react";
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
import { rpcCall, subscribe, type FileChangedEvent } from "@lib/rpc";
import { dirname, basename, join, relative, isDescendant } from "@lib/path";
import { CROSS_TAB_MIME, encodeCrossTabPayload } from "@lib/drag/crossTab";
import { attachDragChip } from "@lib/drag/chip";
import { ContextMenu, type ContextMenuEntry } from "@components/ContextMenu/ContextMenu";
import { ConfirmDialog } from "@components/ConfirmDialog/ConfirmDialog";
import { useToast } from "@lib/toast";
import styles from "./Sidebar.module.css";

const DRAG_MIME = "application/x-jiangyu-path";

type PushToast = ReturnType<typeof useToast>["push"];

type NameValidation = { value: string; error: null } | { value: null; error: string | null };

// Returns `{ value: null, error: null }` for an empty name — callers treat that
// as "silently cancel", not "show an error". Only structurally-bad names (e.g.
// containing `/`) surface an error string.
function validateName(name: string): NameValidation {
  const trimmed = name.trim();
  if (trimmed.length === 0) return { value: null, error: null };
  if (trimmed.includes("/")) return { value: null, error: "Name cannot contain '/'" };
  return { value: trimmed, error: null };
}

function reportRpcError(push: PushToast, action: string, err: unknown): void {
  push({
    variant: "error",
    message: `${action} failed`,
    detail: (err as Error).message,
  });
}

function setDragPath(e: React.DragEvent, path: string): void {
  e.dataTransfer.setData(DRAG_MIME, path);
  e.dataTransfer.effectAllowed = "move";
  attachDragChip(e, basename(path));
}

interface FileEntry {
  readonly name: string;
  readonly path: string;
  readonly isDirectory: boolean;
  readonly isIgnored?: boolean;
}

interface ClipboardState {
  readonly paths: string[];
  readonly mode: "cut" | "copy";
}

interface PendingNew {
  readonly parentPath: string;
  readonly kind: "file" | "folder";
}

// Single fileChanged subscription per Sidebar, with TreeNodes registering
// invalidation callbacks by directory path via `registerRefresh`. Avoids
// O(nodes) listener scans per event when many directories are expanded.
type InvalidateCallback = () => void;

interface SidebarContextValue {
  projectPath: string;
  clipboard: ClipboardState | null;
  setClipboard: (c: ClipboardState | null) => void;
  pendingNew: PendingNew | null;
  setPendingNew: (p: PendingNew | null) => void;
  expandDirectory: (path: string) => void;
  invalidateDir: (path: string) => void;
  registerRefresh: (path: string, cb: InvalidateCallback) => () => void;
  onOpenFile: (path: string) => void;
  dirtyPaths: ReadonlySet<string>;
  onPathMoved: (oldPath: string, newPath: string) => void;
  pushToast: PushToast;
  confirmDelete: (entry: FileEntry) => Promise<boolean>;
  highlightedPath: string | null;
}
const SidebarContext = createContext<SidebarContextValue | null>(null);
function useSidebar(): SidebarContextValue {
  const ctx = use(SidebarContext);
  if (!ctx) throw new Error("SidebarContext missing");
  return ctx;
}

interface SidebarProps {
  projectPath: string;
  onOpenFile: (path: string) => void;
  dirtyPaths: ReadonlySet<string>;
  onPathMoved: (oldPath: string, newPath: string) => void;
  revealPath?: { path: string; tick: number } | null;
}

interface PendingConfirm {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel: string;
  readonly variant: "default" | "danger";
  readonly resolve: (confirmed: boolean) => void;
}

export function Sidebar({
  projectPath,
  onOpenFile,
  dirtyPaths,
  onPathMoved,
  revealPath,
}: SidebarProps) {
  const [width, setWidth] = useState(() => {
    try {
      const saved = localStorage.getItem("jiangyu:sidebarWidth");
      if (saved !== null) {
        const n = Number(saved);
        if (Number.isFinite(n) && n >= 120 && n <= 800) return n;
      }
    } catch {
      /* ignore */
    }
    return 240;
  });
  const [clipboard, setClipboard] = useState<ClipboardState | null>(null);
  const [pendingNew, setPendingNew] = useState<PendingNew | null>(null);
  // Each expand request is a unique-id snapshot of paths-to-open. Children
  // consume by id, so the same id never re-opens a folder the user closed.
  const [expandRequest, setExpandRequest] = useState<{
    id: number;
    paths: ReadonlySet<string>;
  } | null>(null);
  const [rootMenu, setRootMenu] = useState<{ x: number; y: number } | null>(null);
  const [pendingConfirm, setPendingConfirm] = useState<PendingConfirm | null>(null);
  const [highlightedPath, setHighlightedPath] = useState<string | null>(null);
  const draggingRef = useRef(false);
  const widthRef = useRef(width);
  const refreshersRef = useRef<Map<string, Set<InvalidateCallback>>>(new Map());
  const { push: pushToast } = useToast();

  const confirmDelete = useCallback((entry: FileEntry): Promise<boolean> => {
    return new Promise((resolve) => {
      const kind = entry.isDirectory ? "folder" : "file";
      setPendingConfirm({
        title: `Delete ${kind}`,
        message: `Delete ${kind} "${entry.name}"?${entry.isDirectory ? " All contents will be removed." : ""}`,
        confirmLabel: "Delete",
        variant: "danger",
        resolve,
      });
    });
  }, []);

  const registerRefresh = useCallback((path: string, cb: InvalidateCallback) => {
    let set = refreshersRef.current.get(path);
    if (!set) {
      set = new Set();
      refreshersRef.current.set(path, set);
    }
    set.add(cb);
    return () => {
      const s = refreshersRef.current.get(path);
      if (!s) return;
      s.delete(cb);
      if (s.size === 0) refreshersRef.current.delete(path);
    };
  }, []);

  const invalidateDir = useCallback((path: string) => {
    const set = refreshersRef.current.get(path);
    if (!set) return;
    for (const cb of set) cb();
  }, []);

  useEffect(() => {
    return subscribe("fileChanged", (params) => {
      const event = params as FileChangedEvent;
      invalidateDir(dirname(event.path));
    });
  }, [invalidateDir]);

  const expandDirectory = useCallback((path: string) => {
    setExpandRequest((prev) => ({ id: (prev?.id ?? 0) + 1, paths: new Set([path]) }));
  }, []);

  // Reveal file in tree: expand all ancestor dirs and highlight. Computed
  // synchronously off revealPath identity rather than via useEffect so the
  // expand request and highlight stay in lockstep.
  const [prevReveal, setPrevReveal] = useState(revealPath);
  if (prevReveal !== revealPath) {
    setPrevReveal(revealPath);
    if (revealPath?.path.startsWith(projectPath) === true) {
      const filePath = revealPath.path;
      const ancestors = new Set<string>();
      let cur = dirname(filePath);
      while (cur !== projectPath && cur.length > projectPath.length) {
        ancestors.add(cur);
        cur = dirname(cur);
      }
      if (ancestors.size > 0) {
        setExpandRequest((prevReq) => ({ id: (prevReq?.id ?? 0) + 1, paths: ancestors }));
      }
      setHighlightedPath(filePath);
    }
  }

  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    draggingRef.current = true;

    const onMouseMove = (ev: MouseEvent) => {
      if (draggingRef.current) {
        const w = Math.max(160, Math.min(ev.clientX, 600));
        widthRef.current = w;
        setWidth(w);
      }
    };
    const onMouseUp = () => {
      draggingRef.current = false;
      document.removeEventListener("mousemove", onMouseMove);
      document.removeEventListener("mouseup", onMouseUp);
      try {
        localStorage.setItem("jiangyu:sidebarWidth", String(widthRef.current));
      } catch {
        /* ignore */
      }
    };

    document.addEventListener("mousemove", onMouseMove);
    document.addEventListener("mouseup", onMouseUp);
  }, []);

  const sidebarCtx: SidebarContextValue = useMemo(
    () => ({
      projectPath,
      clipboard,
      setClipboard,
      pendingNew,
      setPendingNew,
      expandDirectory,
      invalidateDir,
      registerRefresh,
      onOpenFile,
      dirtyPaths,
      onPathMoved,
      pushToast,
      confirmDelete,
      highlightedPath,
    }),
    [
      projectPath,
      clipboard,
      pendingNew,
      expandDirectory,
      invalidateDir,
      registerRefresh,
      onOpenFile,
      dirtyPaths,
      onPathMoved,
      pushToast,
      confirmDelete,
      highlightedPath,
    ],
  );

  return (
    <SidebarContext value={sidebarCtx}>
      <aside className={styles.sidebar} style={{ width }}>
        <div
          className={styles.tree}
          onContextMenu={(e) => {
            e.preventDefault();
            setRootMenu({ x: e.clientX, y: e.clientY });
          }}
        >
          <TreeNode path={projectPath} depth={0} expandRequest={expandRequest} />
        </div>
        {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- separator is the correct role; the rule doesn't recognise focusable separators. */}
        <div
          className={styles.resizeHandle}
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize sidebar"
          tabIndex={-1}
          onMouseDown={handleMouseDown}
        />
        {rootMenu && (
          <RootContextMenu
            x={rootMenu.x}
            y={rootMenu.y}
            ctx={sidebarCtx}
            onClose={() => setRootMenu(null)}
          />
        )}
        {pendingConfirm && (
          <ConfirmDialog
            title={pendingConfirm.title}
            message={pendingConfirm.message}
            confirmLabel={pendingConfirm.confirmLabel}
            variant={pendingConfirm.variant}
            onConfirm={() => {
              pendingConfirm.resolve(true);
              setPendingConfirm(null);
            }}
            onCancel={() => {
              pendingConfirm.resolve(false);
              setPendingConfirm(null);
            }}
          />
        )}
      </aside>
    </SidebarContext>
  );
}

interface ExpandRequest {
  id: number;
  paths: ReadonlySet<string>;
}

interface TreeNodeProps {
  path: string;
  depth: number;
  expandRequest: ExpandRequest | null;
}

function TreeNode({ path, depth, expandRequest }: TreeNodeProps) {
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [loaded, setLoaded] = useState(false);
  const { registerRefresh, pendingNew } = useSidebar();

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
    return registerRefresh(path, () => setLoaded(false));
  }, [path, registerRefresh]);

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

function DirectoryItem({ entry, depth, expandRequest }: DirectoryItemProps) {
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
}

function FileItem({ entry }: { entry: FileEntry }) {
  const ctx = useSidebar();
  const [renaming, setRenaming] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const isCut = ctx.clipboard?.mode === "cut" && ctx.clipboard.paths.includes(entry.path);
  const isHighlighted = ctx.highlightedPath === entry.path;
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
            {ctx.dirtyPaths.has(entry.path) && <span className={styles.dirtyDot}>●</span>}
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
}

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

interface MenuBuildCtx extends SidebarContextValue {
  startRename: () => void;
}

function RootContextMenu({
  x,
  y,
  ctx,
  onClose,
}: {
  x: number;
  y: number;
  ctx: SidebarContextValue;
  onClose: () => void;
}) {
  const items = useMemo(() => buildRootMenu(ctx), [ctx]);
  return <ContextMenu x={x} y={y} items={items} onClose={onClose} />;
}

function buildRootMenu(ctx: SidebarContextValue): ContextMenuEntry[] {
  const canPaste = ctx.clipboard !== null && ctx.clipboard.paths.length > 0;
  return [
    {
      label: "New File",
      onSelect: () => ctx.setPendingNew({ parentPath: ctx.projectPath, kind: "file" }),
    },
    {
      label: "New Folder",
      onSelect: () => ctx.setPendingNew({ parentPath: ctx.projectPath, kind: "folder" }),
    },
    "separator",
    {
      label: "Paste",
      shortcut: "Ctrl+V",
      disabled: !canPaste,
      onSelect: () => void pasteClipboard({ ...ctx, startRename: () => {} }, ctx.projectPath),
    },
  ];
}

function buildEntryMenu(entry: FileEntry, mctx: MenuBuildCtx): ContextMenuEntry[] {
  const items: ContextMenuEntry[] = [];
  const parentDir = entry.isDirectory ? entry.path : dirname(entry.path);
  const canPaste = mctx.clipboard !== null && mctx.clipboard.paths.length > 0;

  if (entry.isDirectory) {
    items.push(
      {
        label: "New File",
        onSelect: () => {
          mctx.expandDirectory(entry.path);
          mctx.setPendingNew({ parentPath: entry.path, kind: "file" });
        },
      },
      {
        label: "New Folder",
        onSelect: () => {
          mctx.expandDirectory(entry.path);
          mctx.setPendingNew({ parentPath: entry.path, kind: "folder" });
        },
      },
      "separator",
    );
  }

  items.push(
    {
      label: "Reveal in File Explorer",
      onSelect: () => {
        void rpcCall<null>("revealInExplorer", { path: entry.path }).catch((err: unknown) => {
          reportRpcError(mctx.pushToast, "Reveal", err);
        });
      },
    },
    {
      label: "Copy Path",
      onSelect: () => {
        void navigator.clipboard.writeText(entry.path);
      },
    },
    {
      label: "Copy Relative Path",
      onSelect: () => {
        void navigator.clipboard.writeText(relative(mctx.projectPath, entry.path));
      },
    },
    "separator",
    {
      label: "Cut",
      shortcut: "Ctrl+X",
      onSelect: () => mctx.setClipboard({ paths: [entry.path], mode: "cut" }),
    },
    {
      label: "Copy",
      shortcut: "Ctrl+C",
      onSelect: () => mctx.setClipboard({ paths: [entry.path], mode: "copy" }),
    },
    {
      label: "Paste",
      shortcut: "Ctrl+V",
      disabled: !canPaste,
      onSelect: () => void pasteClipboard(mctx, parentDir),
    },
    {
      label: "Duplicate",
      onSelect: () => void duplicate(entry, mctx),
    },
    "separator",
    {
      label: "Rename",
      shortcut: "F2",
      onSelect: mctx.startRename,
    },
    {
      label: "Delete",
      shortcut: "Del",
      onSelect: () => void performDelete(entry, mctx),
    },
  );

  return items;
}

async function pasteClipboard(mctx: MenuBuildCtx, destDir: string): Promise<void> {
  const cb = mctx.clipboard;
  if (!cb) return;
  const method = cb.mode === "cut" ? "movePath" : "copyPath";
  const affectedDirs = new Set<string>([destDir]);
  const failures: string[] = [];
  const results = await Promise.allSettled(
    cb.paths.map((srcPath) => {
      const destPath = join(destDir, basename(srcPath));
      return rpcCall<null>(method, { srcPath, destPath }).then(() => ({ srcPath, destPath }));
    }),
  );
  for (const r of results) {
    if (r.status === "fulfilled") {
      if (cb.mode === "cut") {
        affectedDirs.add(dirname(r.value.srcPath));
        mctx.onPathMoved(r.value.srcPath, r.value.destPath);
      }
    } else {
      failures.push((r.reason as Error).message);
    }
  }
  for (const d of affectedDirs) mctx.invalidateDir(d);
  if (cb.mode === "cut") mctx.setClipboard(null);
  if (failures.length > 0) {
    mctx.pushToast({
      variant: "error",
      message: `Paste failed for ${failures.length} item${failures.length === 1 ? "" : "s"}`,
      detail: failures.join("; "),
    });
  }
}

async function duplicate(entry: FileEntry, ctx: SidebarContextValue): Promise<void> {
  const destPath = copyCandidatePath(entry.path);
  try {
    await rpcCall<null>("copyPath", { srcPath: entry.path, destPath });
    ctx.invalidateDir(dirname(destPath));
  } catch (err) {
    reportRpcError(ctx.pushToast, "Duplicate", err);
  }
}

function copyCandidatePath(path: string): string {
  const dir = dirname(path);
  const name = basename(path);
  const dot = name.lastIndexOf(".");
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const ext = dot > 0 ? name.slice(dot) : "";
  return join(dir, `${stem} copy${ext}`);
}

async function moveWithFeedback(
  srcPath: string,
  destPath: string,
  ctx: SidebarContextValue,
): Promise<void> {
  try {
    await rpcCall<null>("movePath", { srcPath, destPath });
    ctx.onPathMoved(srcPath, destPath);
    ctx.invalidateDir(dirname(srcPath));
    ctx.invalidateDir(dirname(destPath));
  } catch (err) {
    reportRpcError(ctx.pushToast, "Move", err);
  }
}

async function performDelete(entry: FileEntry, ctx: SidebarContextValue): Promise<void> {
  const confirmed = await ctx.confirmDelete(entry);
  if (!confirmed) return;
  try {
    await rpcCall<null>("deletePath", { path: entry.path });
    ctx.invalidateDir(dirname(entry.path));
  } catch (err) {
    reportRpcError(ctx.pushToast, "Delete", err);
  }
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
