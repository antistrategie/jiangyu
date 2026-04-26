import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

function stubStorage() {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => store.set(k, v),
    removeItem: (k: string) => store.delete(k),
    clear: () => store.clear(),
  });
  return store;
}

// Mock the RPC channel — revealProject/closeAllPaneWindows call through it.
vi.mock("@lib/rpc", async (orig) => {
  const actual = await orig<typeof import("@lib/rpc")>();
  return {
    ...actual,
    rpcCall: vi.fn().mockResolvedValue(null),
    subscribe: vi.fn(() => () => {}),
  };
});

// The project picker RPC wrapper — stub it so tests don't open a dialog.
vi.mock("./commands", () => ({
  pickProjectFolder: vi.fn(),
}));

import { rpcCall } from "@lib/rpc";
import { pickProjectFolder } from "./commands";
import { useProjectStore } from "./store";
import { useLayoutStore } from "@lib/panes/layoutStore";
import { usePaneWindowStore } from "@lib/panes/paneWindowStore";
import { EMPTY_LAYOUT } from "@lib/layout";

const mockRpc = vi.mocked(rpcCall);
const mockPick = vi.mocked(pickProjectFolder);

describe("projectStore", () => {
  beforeEach(() => {
    stubStorage();
    mockRpc.mockClear();
    mockRpc.mockResolvedValue(null);
    mockPick.mockReset();
    useProjectStore.setState({ projectPath: null, recentProjects: [] });
    useLayoutStore.setState({
      layout: EMPTY_LAYOUT,
      currentProject: null,
      fullscreenPaneId: null,
      lastCodePath: null,
      revealRequest: null,
    });
    usePaneWindowStore.setState({ windows: {} });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe("switchProject", () => {
    it("sets projectPath and updates recentProjects", () => {
      useProjectStore.getState().switchProject("/proj-a");
      const s = useProjectStore.getState();
      expect(s.projectPath).toBe("/proj-a");
      expect(s.recentProjects[0]).toBe("/proj-a");
    });

    it("updates layoutStore.currentProject", () => {
      useProjectStore.getState().switchProject("/proj-a");
      expect(useLayoutStore.getState().currentProject).toBe("/proj-a");
    });

    it("closes every pane window on the host side", () => {
      useProjectStore.getState().switchProject("/proj-a");
      expect(mockRpc).toHaveBeenCalledWith("closeAllPaneWindows");
    });
  });

  describe("closeProject", () => {
    it("clears projectPath + layoutStore.currentProject and fires the close RPC", () => {
      useProjectStore.getState().switchProject("/proj-a");
      mockRpc.mockClear();

      useProjectStore.getState().closeProject();
      expect(useProjectStore.getState().projectPath).toBeNull();
      expect(useLayoutStore.getState().currentProject).toBeNull();
      expect(mockRpc).toHaveBeenCalledWith("closeAllPaneWindows");
    });
  });

  describe("revealProject", () => {
    it("no-ops when no project is open", () => {
      useProjectStore.getState().revealProject();
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("calls revealInExplorer with the open path", () => {
      useProjectStore.setState({ projectPath: "/proj-a" });
      useProjectStore.getState().revealProject();
      expect(mockRpc).toHaveBeenCalledWith("revealInExplorer", { path: "/proj-a" });
    });
  });

  describe("openProject", () => {
    it("switches to the picked path", async () => {
      mockPick.mockResolvedValueOnce("/picked");
      await useProjectStore.getState().openProject();
      expect(useProjectStore.getState().projectPath).toBe("/picked");
    });

    it("is a no-op when the picker is cancelled", async () => {
      mockPick.mockResolvedValueOnce(null);
      await useProjectStore.getState().openProject();
      expect(useProjectStore.getState().projectPath).toBeNull();
    });
  });
});
