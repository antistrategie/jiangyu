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

vi.mock("@lib/rpc", async (orig) => {
  const actual = await orig<typeof import("@lib/rpc")>();
  return {
    ...actual,
    rpcCall: vi.fn(),
    subscribe: vi.fn(() => () => {}),
  };
});

import { rpcCall } from "@lib/rpc";
import { usePaneWindowStore } from "./paneWindowStore";
import { savePaneWindows, type PaneWindowDescriptor } from "./paneWindowStorage";

const mockRpc = vi.mocked(rpcCall);

const codeDesc: PaneWindowDescriptor = {
  kind: "code",
  filePaths: ["/a"],
  activeFilePath: "/a",
};

describe("paneWindowStore", () => {
  beforeEach(() => {
    stubStorage();
    mockRpc.mockReset();
    usePaneWindowStore.setState({ windows: {} });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe("openPaneWindow", () => {
    it("records the descriptor under the returned windowId on success", async () => {
      mockRpc.mockResolvedValueOnce({ windowId: "win-1" });
      await usePaneWindowStore.getState().openPaneWindow(codeDesc);
      expect(usePaneWindowStore.getState().windows["win-1"]).toBe(codeDesc);
    });

    it("is a no-op when the host returns an empty windowId", async () => {
      mockRpc.mockResolvedValueOnce({ windowId: "" });
      await usePaneWindowStore.getState().openPaneWindow(codeDesc);
      expect(Object.keys(usePaneWindowStore.getState().windows)).toEqual([]);
    });

    it("swallows RPC errors and leaves the state unchanged", async () => {
      mockRpc.mockRejectedValueOnce(new Error("no"));
      await usePaneWindowStore.getState().openPaneWindow(codeDesc);
      expect(Object.keys(usePaneWindowStore.getState().windows)).toEqual([]);
    });
  });

  describe("closeAllPaneWindows", () => {
    it("fires closeAllPaneWindows RPC and clears the in-memory map", () => {
      usePaneWindowStore.setState({ windows: { "win-1": codeDesc } });
      mockRpc.mockResolvedValueOnce(null);
      usePaneWindowStore.getState().closeAllPaneWindows();
      expect(usePaneWindowStore.getState().windows).toEqual({});
      expect(mockRpc).toHaveBeenCalledWith("closeAllPaneWindows");
    });
  });

  describe("_handleClosed", () => {
    it("drops the entry matching the windowId", () => {
      usePaneWindowStore.setState({ windows: { "win-1": codeDesc, "win-2": codeDesc } });
      usePaneWindowStore.getState()._handleClosed("win-1");
      expect(usePaneWindowStore.getState().windows["win-1"]).toBeUndefined();
      expect(usePaneWindowStore.getState().windows["win-2"]).toBe(codeDesc);
    });

    it("is a no-op for an unknown windowId", () => {
      const before = usePaneWindowStore.getState();
      usePaneWindowStore.getState()._handleClosed("never");
      expect(usePaneWindowStore.getState()).toBe(before);
    });
  });

  describe("_handleTabsChanged", () => {
    it("replaces filePaths + activeFilePath while preserving kind/browserState", () => {
      usePaneWindowStore.setState({
        windows: { "win-1": { ...codeDesc, browserState: undefined } },
      });
      usePaneWindowStore.getState()._handleTabsChanged("win-1", ["/new", "/other"], "/new");
      const d = usePaneWindowStore.getState().windows["win-1"]!;
      expect(d.kind).toBe("code");
      expect(d.filePaths).toEqual(["/new", "/other"]);
      expect(d.activeFilePath).toBe("/new");
    });

    it("creates a default code descriptor when the windowId wasn't tracked", () => {
      usePaneWindowStore.getState()._handleTabsChanged("new-win", ["/a"], "/a");
      const d = usePaneWindowStore.getState().windows["new-win"]!;
      expect(d.kind).toBe("code");
      expect(d.filePaths).toEqual(["/a"]);
    });
  });

  describe("_handleBrowserStateChanged", () => {
    it("updates the browserState on an existing window", () => {
      const browserDesc: PaneWindowDescriptor = {
        kind: "assetBrowser",
        filePaths: [],
        activeFilePath: null,
      };
      usePaneWindowStore.setState({ windows: { "win-1": browserDesc } });
      const nextState = {
        query: "soldier",
        kindFilter: "model" as const,
        selection: [],
        focusedKey: null,
        listFraction: 0.5,
        scrollTop: 0,
      };
      usePaneWindowStore.getState()._handleBrowserStateChanged("win-1", nextState);
      expect(usePaneWindowStore.getState().windows["win-1"]!.browserState).toBe(nextState);
    });

    it("is a no-op for an unknown window", () => {
      const before = usePaneWindowStore.getState();
      usePaneWindowStore.getState()._handleBrowserStateChanged("ghost", {
        query: "",
        kindFilter: "all",
        selection: [],
        focusedKey: null,
        listFraction: 0.35,
        scrollTop: 0,
      });
      expect(usePaneWindowStore.getState()).toBe(before);
    });
  });

  describe("restoreFor", () => {
    it("loads persisted descriptors and re-opens each via openPaneWindow", async () => {
      savePaneWindows("/proj", [codeDesc]);
      mockRpc.mockResolvedValueOnce({ windowId: "win-restored" });
      await usePaneWindowStore.getState().restoreFor("/proj");
      expect(mockRpc).toHaveBeenCalledWith(
        "openPaneWindow",
        expect.objectContaining({ filePaths: codeDesc.filePaths }),
      );
      expect(usePaneWindowStore.getState().windows["win-restored"]).toEqual(codeDesc);
    });

    it("is a no-op when nothing is persisted", async () => {
      await usePaneWindowStore.getState().restoreFor("/empty");
      expect(mockRpc).not.toHaveBeenCalled();
    });
  });
});
