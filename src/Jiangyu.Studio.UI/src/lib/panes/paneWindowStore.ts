import { useEffect } from "react";
import { create } from "zustand";
import { rpcCall, subscribe } from "@lib/rpc.ts";
import {
  loadPaneWindows,
  savePaneWindows,
  type PaneWindowDescriptor,
} from "./paneWindowStorage.ts";
import type { AssetBrowserState, TemplateBrowserState } from "./browserState.ts";

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
    try {
      const result = await rpcCall<{ windowId: string }>("openPaneWindow", {
        kind: desc.kind,
        filePaths: desc.filePaths,
        activeFilePath: desc.activeFilePath,
        browserState: desc.browserState,
      });
      if (result.windowId.length === 0) return;
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
interface PaneWindowClosed {
  readonly windowId: string;
}
interface PaneWindowTabsChanged {
  readonly windowId: string;
  readonly filePaths: readonly string[];
  readonly activeFilePath: string | null;
}
interface PaneWindowBrowserStateChanged {
  readonly windowId: string;
  readonly state: AssetBrowserState | TemplateBrowserState;
}

subscribe("paneWindowClosed", (params) => {
  const { windowId } = params as PaneWindowClosed;
  usePaneWindowStore.getState()._handleClosed(windowId);
});
subscribe("paneWindowTabsChanged", (params) => {
  const { windowId, filePaths, activeFilePath } = params as PaneWindowTabsChanged;
  usePaneWindowStore.getState()._handleTabsChanged(windowId, filePaths, activeFilePath);
});
subscribe("paneWindowBrowserStateChanged", (params) => {
  const { windowId, state } = params as PaneWindowBrowserStateChanged;
  usePaneWindowStore.getState()._handleBrowserStateChanged(windowId, state);
});

// App calls this once with the current project; we remember it for persist()
// so store mutations write to the right localStorage slot. Also kicks off
// restoreFor when the project changes (load-on-open behaviour).
export function useSyncPaneWindowProject(projectPath: string | null): void {
  useEffect(() => {
    currentProject = projectPath;
    if (projectPath === null) return;
    void usePaneWindowStore.getState().restoreFor(projectPath);
  }, [projectPath]);
}
