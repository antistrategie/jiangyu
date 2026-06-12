import { createContext, use } from "react";
import type { SidebarOpsContext } from "./fileOps";

export interface PendingNew {
  readonly parentPath: string;
  readonly kind: "file" | "folder";
}

// Single fileChanged subscription per Sidebar, with TreeNodes registering
// invalidation callbacks by directory path via `registerRefresh`. Avoids
// O(nodes) listener scans per event when many directories are expanded.
export type InvalidateCallback = () => void;

// Stable sidebar state: everything here changes only on explicit user
// actions (cut/copy, new entry, project switch), so a context update is an
// acceptable whole-tree re-render.
export interface SidebarContextValue extends SidebarOpsContext {
  readonly projectPath: string;
  readonly pendingNew: PendingNew | null;
  readonly setPendingNew: (p: PendingNew | null) => void;
  readonly expandDirectory: (path: string) => void;
  readonly registerRefresh: (path: string, cb: InvalidateCallback) => () => void;
  readonly onOpenFile: (path: string) => void;
}

export const SidebarContext = createContext<SidebarContextValue | null>(null);

export function useSidebar(): SidebarContextValue {
  const ctx = use(SidebarContext);
  if (!ctx) throw new Error("SidebarContext missing");
  return ctx;
}

// Volatile per-row display state, kept out of SidebarContext so a reveal or
// a dirty flip doesn't re-render every visible row. Only the thin FileItem
// wrappers consume this context, and each forwards its row's booleans to a
// memoised row component that bails unless its own flags changed.
export interface SidebarVolatileValue {
  readonly highlightedPath: string | null;
  readonly dirtyPaths: ReadonlySet<string>;
}

export const SidebarVolatileContext = createContext<SidebarVolatileValue>({
  highlightedPath: null,
  dirtyPaths: new Set(),
});
