import { useEffect } from "react";
import { create } from "zustand";
import { rpcCall, subscribe } from "@shared/rpc";
import { loadPaneWindows, savePaneWindows, type PaneWindowDescriptor } from "./paneWindowStorage";
import type { AssetBrowserState, TemplateBrowserState } from "./browserState";

interface PaneWindowStore {
  /** windowId → descriptor for every pane window spawned this session. */
  readonly windows: Readonly<Record<string, PaneWindowDescriptor>>;

  /** Spawn a new pane window for the given descriptor. */
  readonly openPaneWindow: (desc: PaneWindowDescriptor) => Promise<void>;
  /** Close every pane window and clear the in-memory index. Used on project close/switch. */
  readonly closeAllPaneWindows: () => void;
  /** Restore every persisted pane window for `projectPath`. Idempotent — clears first. */
  readonly restoreFor: (projectPath: string) => Promise<void>;

  // Internal actions called by the host-notification subscriptions below.
  readonly _handleClosed: (windowId: string) => void;
  readonly _handleTabsChanged: (
    windowId: string,
    filePaths: readonly string[],
    activeFilePath: string | null,
  ) => void;
  readonly _handleBrowserStateChanged: (
    windowId: string,
    state: AssetBrowserState | TemplateBrowserState,
  ) => void;
}

// Keeps the currentProject pointer module-local — the zustand store doesn't
// own it (projectStore does), but persist() needs a stable reference to know
// which localStorage slot to write. Updated via useSyncPaneWindowProject().
let currentProject: string | null = null;

function persist(windows: Readonly<Record<string, PaneWindowDescriptor>>): void {
  if (currentProject === null) return;
  savePaneWindows(currentProject, Object.values(windows));
}

export const usePaneWindowStore = create<PaneWindowStore>((set, get) => ({
  windows: {},

  openPaneWindow: async (desc) => {
    const project = currentProject;
    try {
      const result = await rpcCall<{ windowId: string }>("openPaneWindow", {
        kind: desc.kind,
        filePaths: desc.filePaths,
        activeFilePath: desc.activeFilePath,
        browserState: desc.browserState,
      });
      if (result.windowId.length === 0) return;
      // The project can switch while the host opens the window. The window
      // belongs to the old session, so don't track it (tracking would
      // persist it into the new project's slot).
      if (currentProject !== project) return;
      set((s) => {
        const windows = { ...s.windows, [result.windowId]: desc };
        persist(windows);
        return { windows };
      });
    } catch (err) {
      console.error("[paneWindowStore] openPaneWindow failed:", err);
    }
  },

  closeAllPaneWindows: () => {
    void rpcCall<null>("closeAllPaneWindows").catch(() => {});
    set({ windows: {} });
  },

  restoreFor: async (projectPath) => {
    const stored = loadPaneWindows(projectPath);
    if (stored.length === 0) return;
    set({ windows: {} });
    for (const desc of stored) {
      // Rapid project switches interleave with the awaits below. A stale
      // restore loop must stop rather than keep spawning the old project's
      // windows (and persisting them into the new project's slot).
      if (currentProject !== projectPath) return;
      await get().openPaneWindow(desc);
    }
  },

  _handleClosed: (windowId) => {
    set((s) => {
      if (!(windowId in s.windows)) return s;
      const { [windowId]: _removed, ...rest } = s.windows;
      void _removed;
      persist(rest);
      return { windows: rest };
    });
  },

  _handleTabsChanged: (windowId, filePaths, activeFilePath) => {
    set((s) => {
      const existing = s.windows[windowId];
      const next: PaneWindowDescriptor = {
        kind: existing?.kind ?? "code",
        filePaths,
        activeFilePath,
        browserState: existing?.browserState,
      };
      const windows = { ...s.windows, [windowId]: next };
      persist(windows);
      return { windows };
    });
  },

  _handleBrowserStateChanged: (windowId, state) => {
    set((s) => {
      const existing = s.windows[windowId];
      if (existing === undefined) return s;
      const windows = { ...s.windows, [windowId]: { ...existing, browserState: state } };
      persist(windows);
      return { windows };
    });
  },
}));

// Module-level subscriptions: the host fans three notifications into every
// window. We translate each into a store action. Set up once at module load;
// there's only ever one pane-window store per process.
export interface PaneWindowClosed {
  readonly windowId: string;
}
export interface PaneWindowTabsChanged {
  readonly windowId: string;
  readonly filePaths: readonly string[];
  readonly activeFilePath: string | null;
}
export interface PaneWindowBrowserStateChanged {
  readonly windowId: string;
  readonly state: AssetBrowserState | TemplateBrowserState;
}

declare module "@shared/rpc/notifications" {
  interface HostNotificationMap {
    paneWindowClosed: PaneWindowClosed;
    paneWindowTabsChanged: PaneWindowTabsChanged;
    paneWindowBrowserStateChanged: PaneWindowBrowserStateChanged;
  }
}

subscribe("paneWindowClosed", ({ windowId }) => {
  usePaneWindowStore.getState()._handleClosed(windowId);
});
subscribe("paneWindowTabsChanged", ({ windowId, filePaths, activeFilePath }) => {
  usePaneWindowStore.getState()._handleTabsChanged(windowId, filePaths, activeFilePath);
});
subscribe("paneWindowBrowserStateChanged", ({ windowId, state }) => {
  usePaneWindowStore.getState()._handleBrowserStateChanged(windowId, state);
});

// Updates the module-local project pointer that persist() and the restore /
// open guards compare against. Exported for tests, app code goes through
// useSyncPaneWindowProject.
export function setPaneWindowProject(projectPath: string | null): void {
  currentProject = projectPath;
}

// App calls this once with the current project; we remember it for persist()
// so store mutations write to the right localStorage slot. Also kicks off
// restoreFor when the project changes (load-on-open behaviour).
export function useSyncPaneWindowProject(projectPath: string | null): void {
  useEffect(() => {
    setPaneWindowProject(projectPath);
    if (projectPath === null) return;
    void usePaneWindowStore.getState().restoreFor(projectPath);
  }, [projectPath]);
}
