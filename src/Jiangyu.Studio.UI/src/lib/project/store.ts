import { create } from "zustand";
import { rpcCall } from "@lib/rpc.ts";
import { useLayoutStore } from "@lib/panes/layoutStore.ts";
import { usePaneWindowStore } from "@lib/panes/paneWindowStore.ts";
import { pickProjectFolder } from "./commands.ts";
import { loadRecentProjects, recordRecentProject } from "./recent.ts";

interface ProjectStore {
  /** Absolute path to the open project root, or null when none is open. */
  readonly projectPath: string | null;
  /** Most-recently-opened paths, newest first (mirrored in localStorage). */
  readonly recentProjects: readonly string[];

  /** Open the OS folder picker and switch to the chosen project (no-op on cancel). */
  readonly openProject: () => Promise<void>;
  /** Switch to a project path the caller already has (e.g. recent list click). */
  readonly switchProject: (path: string) => void;
  /** Close the current project — tears down layout + pane windows on the host side. */
  readonly closeProject: () => void;
  /** Reveal the project root in the OS file explorer. */
  readonly revealProject: () => void;
}

// Coordinates with layoutStore on switch/close so the two always agree on
// "which project is open" — and fires the host RPC that closes every
// secondary pane window, since those are scoped to the previous project.
export const useProjectStore = create<ProjectStore>((set, get) => ({
  projectPath: null,
  recentProjects: loadRecentProjects(),

  openProject: async () => {
    const path = await pickProjectFolder();
    if (path !== null) get().switchProject(path);
  },

  switchProject: (path) => {
    usePaneWindowStore.getState().closeAllPaneWindows();
    useLayoutStore.getState().setProject(path);
    set({ projectPath: path, recentProjects: recordRecentProject(path) });
  },

  closeProject: () => {
    usePaneWindowStore.getState().closeAllPaneWindows();
    useLayoutStore.getState().setProject(null);
    set({ projectPath: null });
  },

  revealProject: () => {
    const path = get().projectPath;
    if (path === null) return;
    void rpcCall<null>("revealInExplorer", { path }).catch((err) => {
      console.error("[projectStore] reveal failed:", err);
    });
  },
}));
