import { useState, useEffect, useCallback, useRef, useMemo } from "react";
import { subscribe } from "@shared/rpc";
import { dirname } from "@shared/path";
import { ContextMenu } from "@shared/ui/ContextMenu/ContextMenu";
import { ConfirmDialog } from "@shared/ui/ConfirmDialog/ConfirmDialog";
import { useToastPush } from "@shared/toast";
import type { ClipboardState, FileEntry } from "./fileOps";
import { buildRootMenu } from "./sidebarMenus";
import { TreeNode, type ExpandRequest } from "./FileTree";
import {
  SidebarContext,
  SidebarVolatileContext,
  type InvalidateCallback,
  type PendingNew,
  type SidebarContextValue,
  type SidebarVolatileValue,
} from "./sidebarContext";
import styles from "./Sidebar.module.css";

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
  const [expandRequest, setExpandRequest] = useState<ExpandRequest | null>(null);
  const [rootMenu, setRootMenu] = useState<{ x: number; y: number } | null>(null);
  const [pendingConfirm, setPendingConfirm] = useState<PendingConfirm | null>(null);
  const [highlightedPath, setHighlightedPath] = useState<string | null>(null);
  const asideRef = useRef<HTMLElement>(null);
  const draggingRef = useRef(false);
  const widthRef = useRef(width);
  const refreshersRef = useRef<Map<string, Set<InvalidateCallback>>>(new Map());
  const pushToast = useToastPush();

  const confirmDelete = useCallback((entry: FileEntry): Promise<boolean> => {
    return new Promise((resolve) => {
      const kind = entry.isDirectory ? "folder" : "file";
      setPendingConfirm({
        title: `Delete ${kind}`,
        message: `Delete ${kind} "${entry.name}"?${entry.isDirectory ? " All contents will be removed." : ""}`,
        confirmLabel: "Delete",
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
    return subscribe("fileChanged", (event) => {
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
      if (!draggingRef.current) return;
      const w = Math.max(160, Math.min(ev.clientX, 600));
      widthRef.current = w;
      // Write straight to the DOM during the drag: committing React state
      // per mousemove re-renders the whole tree once per pixel.
      if (asideRef.current !== null) asideRef.current.style.width = `${w}px`;
    };
    const onMouseUp = () => {
      draggingRef.current = false;
      document.removeEventListener("mousemove", onMouseMove);
      document.removeEventListener("mouseup", onMouseUp);
      // Commit once: state for the next render, localStorage for the next session.
      setWidth(widthRef.current);
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
      onPathMoved,
      pushToast,
      confirmDelete,
    }),
    [
      projectPath,
      clipboard,
      pendingNew,
      expandDirectory,
      invalidateDir,
      registerRefresh,
      onOpenFile,
      onPathMoved,
      pushToast,
      confirmDelete,
    ],
  );

  // Reveal highlights and dirty flips ride a separate context so they only
  // re-render the thin per-row wrappers (see FileTree's FileItem).
  const volatileCtx: SidebarVolatileValue = useMemo(
    () => ({ highlightedPath, dirtyPaths }),
    [highlightedPath, dirtyPaths],
  );

  return (
    <SidebarContext value={sidebarCtx}>
      <SidebarVolatileContext value={volatileCtx}>
        <aside ref={asideRef} className={styles.sidebar} style={{ width }}>
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
      </SidebarVolatileContext>
    </SidebarContext>
  );
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
