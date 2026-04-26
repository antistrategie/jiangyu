import { create } from "zustand";
import {
  EMPTY_LAYOUT,
  closePane,
  closeTabs,
  closeTabsEverywhere,
  convertPane,
  findPane,
  insertCrossWindowPane,
  loadLayout,
  moveTab,
  movePaneToEdge,
  openFile,
  remapPaths,
  reorderTab,
  saveLayout,
  selectTab,
  setActivePane,
  setBrowserPaneState,
  setColumnWeight,
  setPaneWeight,
  splitAtEdgeWithPath,
  splitDown,
  splitRight,
  splitWithTab,
  swapPanes,
  type Layout,
  type PaneKind,
  type SplitEdge,
} from "@lib/layout";
import type { AssetBrowserState, TemplateBrowserState } from "./browserState";
import type { CrossPanePayload } from "@lib/drag/crossPane";
import { loadSessionRestoreTabs } from "@lib/settings";

interface RevealRequest {
  readonly path: string;
  readonly tick: number;
}

interface LayoutStore {
  // --- State ---
  readonly layout: Layout;
  /** The project the current layout belongs to. Null when no project is open. */
  readonly currentProject: string | null;
  readonly fullscreenPaneId: string | null;
  /** Last code file to get focus. Sidebar reveal + TemplateBrowser "active file" rely on this. */
  readonly lastCodePath: string | null;
  /** Tick-based signal for the sidebar to re-reveal. Null when cleared. */
  readonly revealRequest: RevealRequest | null;

  // --- Lifecycle ---
  /** Load the layout associated with `path` from localStorage. Pass null to clear. */
  readonly setProject: (path: string | null) => void;

  // --- Layout transforms ---
  readonly openFile: (path: string, paneId?: string) => void;
  readonly selectTab: (paneId: string, path: string) => void;
  readonly setActivePane: (paneId: string) => void;
  readonly closeTabsInPane: (paneId: string, paths: readonly string[]) => void;
  readonly closeTabsEverywhere: (paths: readonly string[]) => void;
  readonly moveTab: (fromPaneId: string, toPaneId: string, path: string) => void;
  readonly reorderTab: (paneId: string, path: string, targetIndex: number) => void;
  readonly closePane: (paneId: string) => void;
  readonly splitRight: (kind?: PaneKind) => void;
  readonly splitDown: (kind?: PaneKind) => void;
  readonly splitFromPane: (paneId: string, direction: "right" | "down") => void;
  readonly splitWithTab: (
    fromPaneId: string,
    toPaneId: string,
    path: string,
    edge: SplitEdge,
  ) => void;
  readonly splitAtEdgeWithPath: (toPaneId: string, path: string, edge: SplitEdge) => void;
  readonly movePaneToEdge: (paneId: string, targetPaneId: string, edge: SplitEdge) => void;
  readonly swapPanes: (paneIdA: string, paneIdB: string) => void;
  readonly convertPane: (paneId: string, kind: PaneKind) => void;
  readonly resizeColumns: (
    leftId: string,
    leftWeight: number,
    rightId: string,
    rightWeight: number,
  ) => void;
  readonly resizePanes: (
    topId: string,
    topWeight: number,
    bottomId: string,
    bottomWeight: number,
  ) => void;
  readonly setBrowserPaneState: (
    paneId: string,
    state: AssetBrowserState | TemplateBrowserState,
  ) => void;
  readonly insertCrossWindowPane: (
    toPaneId: string,
    payload: CrossPanePayload,
    edge: "left" | "right" | "top" | "bottom",
  ) => void;
  readonly remapPaths: (remap: (p: string) => string) => void;

  // --- Fullscreen ---
  readonly toggleFullscreen: (paneId: string) => void;
  readonly setFullscreenPaneId: (id: string | null) => void;
}

// Bump each time a caller asks for a reveal, so the sidebar's effect can
// dedupe: same tick = no-op, new tick = expand tree + briefly highlight.
let revealTick = 0;

export const useLayoutStore = create<LayoutStore>((set) => {
  const revealForPath = (path: string): RevealRequest => {
    revealTick += 1;
    return { path, tick: revealTick };
  };

  return {
    layout: EMPTY_LAYOUT,
    currentProject: null,
    fullscreenPaneId: null,
    lastCodePath: null,
    revealRequest: null,

    setProject: (path) => {
      // Honour the session-restore setting: when disabled, open projects
      // fresh instead of rehydrating the saved pane/tab layout. Saving
      // still happens unconditionally below, so toggling the setting back
      // on later surfaces whatever the user edited most recently.
      const restore = path !== null && loadSessionRestoreTabs();
      const layout = restore ? (loadLayout(path) ?? EMPTY_LAYOUT) : EMPTY_LAYOUT;
      set({
        currentProject: path,
        layout,
        fullscreenPaneId: null,
        lastCodePath: null,
        revealRequest: null,
      });
    },

    openFile: (path, paneId) => {
      set((s) => ({
        layout: openFile(s.layout, path, paneId ?? null),
        lastCodePath: path,
        revealRequest: revealForPath(path),
      }));
    },

    selectTab: (paneId, path) => {
      set((s) => ({
        layout: selectTab(s.layout, paneId, path),
        lastCodePath: path,
        revealRequest: revealForPath(path),
      }));
    },

    setActivePane: (paneId) => {
      set((s) => {
        const nextLayout = setActivePane(s.layout, paneId);
        const pane = findPane(nextLayout, paneId);
        const nextActive = pane?.kind === "code" ? pane.activeTab : null;
        if (nextActive !== null) {
          return {
            layout: nextLayout,
            lastCodePath: nextActive,
            revealRequest: revealForPath(nextActive),
          };
        }
        return { layout: nextLayout };
      });
    },

    closeTabsInPane: (paneId, paths) => {
      set((s) => ({ layout: closeTabs(s.layout, paneId, paths) }));
    },

    closeTabsEverywhere: (paths) => {
      set((s) => ({ layout: closeTabsEverywhere(s.layout, paths) }));
    },

    moveTab: (fromPaneId, toPaneId, path) => {
      set((s) => ({ layout: moveTab(s.layout, fromPaneId, toPaneId, path) }));
    },

    reorderTab: (paneId, path, targetIndex) => {
      set((s) => ({ layout: reorderTab(s.layout, paneId, path, targetIndex) }));
    },

    closePane: (paneId) => {
      set((s) => ({ layout: closePane(s.layout, paneId) }));
    },

    splitRight: (kind = "code") => {
      set((s) => ({ layout: splitRight(s.layout, kind) }));
    },

    splitDown: (kind = "code") => {
      set((s) => ({ layout: splitDown(s.layout, kind) }));
    },

    splitFromPane: (paneId, direction) => {
      set((s) => {
        const withActive = setActivePane(s.layout, paneId);
        return {
          layout: direction === "right" ? splitRight(withActive) : splitDown(withActive),
        };
      });
    },

    splitWithTab: (fromPaneId, toPaneId, path, edge) => {
      set((s) => ({ layout: splitWithTab(s.layout, fromPaneId, toPaneId, path, edge) }));
    },

    splitAtEdgeWithPath: (toPaneId, path, edge) => {
      set((s) => ({ layout: splitAtEdgeWithPath(s.layout, toPaneId, path, edge) }));
    },

    movePaneToEdge: (paneId, targetPaneId, edge) => {
      set((s) => ({ layout: movePaneToEdge(s.layout, paneId, targetPaneId, edge) }));
    },

    swapPanes: (paneIdA, paneIdB) => {
      set((s) => ({ layout: swapPanes(s.layout, paneIdA, paneIdB) }));
    },

    convertPane: (paneId, kind) => {
      set((s) => ({ layout: convertPane(s.layout, paneId, kind) }));
    },

    resizeColumns: (leftId, leftWeight, rightId, rightWeight) => {
      set((s) => {
        const next = setColumnWeight(s.layout, leftId, leftWeight);
        return { layout: setColumnWeight(next, rightId, rightWeight) };
      });
    },

    resizePanes: (topId, topWeight, bottomId, bottomWeight) => {
      set((s) => {
        const next = setPaneWeight(s.layout, topId, topWeight);
        return { layout: setPaneWeight(next, bottomId, bottomWeight) };
      });
    },

    setBrowserPaneState: (paneId, state) => {
      set((s) => ({ layout: setBrowserPaneState(s.layout, paneId, state) }));
    },

    insertCrossWindowPane: (toPaneId, payload, edge) => {
      set((s) => ({
        layout: insertCrossWindowPane(
          s.layout,
          toPaneId,
          payload.kind,
          (payload.filePaths ?? []).map((p) => ({ path: p })),
          payload.activeFilePath ?? null,
          payload.browserState,
          edge,
        ),
      }));
    },

    remapPaths: (remap) => {
      set((s) => ({ layout: remapPaths(s.layout, remap) }));
    },

    toggleFullscreen: (paneId) => {
      set((s) => ({ fullscreenPaneId: s.fullscreenPaneId === paneId ? null : paneId }));
    },

    setFullscreenPaneId: (id) => {
      set({ fullscreenPaneId: id });
    },
  };
});

// Debounced autosave: wait 150ms of idle to avoid pounding localStorage
// during drag-resize (which fires one layout update per pixel). The save
// skips when project didn't change — setProject triggers a layout swap
// mid-stream and we don't want to save the newly-loaded layout back
// immediately.
let saveTimer: ReturnType<typeof setTimeout> | null = null;
useLayoutStore.subscribe((state, prev) => {
  // Clear fullscreen when the target pane leaves the layout.
  if (state.fullscreenPaneId !== null && state.layout !== prev.layout) {
    const stillExists = state.layout.columns.some((c) =>
      c.panes.some((p) => p.id === state.fullscreenPaneId),
    );
    if (!stillExists) {
      useLayoutStore.setState({ fullscreenPaneId: null });
    }
  }

  // Debounced save (only when project unchanged + layout changed).
  if (
    state.currentProject !== null &&
    state.currentProject === prev.currentProject &&
    state.layout !== prev.layout
  ) {
    const project = state.currentProject;
    if (saveTimer !== null) clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      saveLayout(project, useLayoutStore.getState().layout);
      saveTimer = null;
    }, 150);
  }
});
