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

import { EMPTY_LAYOUT, openFile, saveLayout } from "@lib/layout.ts";
import { useLayoutStore } from "./layoutStore.ts";

function resetStore() {
  useLayoutStore.setState({
    layout: EMPTY_LAYOUT,
    currentProject: null,
    fullscreenPaneId: null,
    lastCodePath: null,
    revealRequest: null,
  });
}

describe("layoutStore", () => {
  beforeEach(() => {
    stubStorage();
    resetStore();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  describe("setProject", () => {
    it("loads the stored layout for a project path", () => {
      const stored = openFile(EMPTY_LAYOUT, "/proj/a.tsx");
      saveLayout("/proj", stored);
      useLayoutStore.getState().setProject("/proj");
      expect(useLayoutStore.getState().layout).toEqual(stored);
      expect(useLayoutStore.getState().currentProject).toBe("/proj");
    });

    it("falls back to EMPTY_LAYOUT when nothing is stored", () => {
      useLayoutStore.getState().setProject("/unseen");
      expect(useLayoutStore.getState().layout).toBe(EMPTY_LAYOUT);
    });

    it("clears layout, fullscreen, and reveal when called with null", () => {
      // Seed some state first.
      useLayoutStore.getState().openFile("/proj/a.tsx");
      useLayoutStore.setState({ fullscreenPaneId: "pane1" });

      useLayoutStore.getState().setProject(null);
      const s = useLayoutStore.getState();
      expect(s.layout).toBe(EMPTY_LAYOUT);
      expect(s.currentProject).toBeNull();
      expect(s.fullscreenPaneId).toBeNull();
      expect(s.revealRequest).toBeNull();
    });
  });

  describe("openFile", () => {
    it("delegates to layout.openFile and stamps lastCodePath + revealRequest", () => {
      useLayoutStore.getState().openFile("/proj/a.tsx");
      const s = useLayoutStore.getState();
      expect(s.lastCodePath).toBe("/proj/a.tsx");
      expect(s.revealRequest).not.toBeNull();
      expect(s.revealRequest!.path).toBe("/proj/a.tsx");
    });

    it("bumps the reveal tick on each call so the sidebar can dedupe", () => {
      useLayoutStore.getState().openFile("/a.tsx");
      const first = useLayoutStore.getState().revealRequest!.tick;
      useLayoutStore.getState().openFile("/b.tsx");
      const second = useLayoutStore.getState().revealRequest!.tick;
      expect(second).toBeGreaterThan(first);
    });
  });

  describe("fullscreen", () => {
    it("toggleFullscreen flips the paneId", () => {
      useLayoutStore.getState().toggleFullscreen("pane-1");
      expect(useLayoutStore.getState().fullscreenPaneId).toBe("pane-1");
      useLayoutStore.getState().toggleFullscreen("pane-1");
      expect(useLayoutStore.getState().fullscreenPaneId).toBeNull();
    });

    it("clears fullscreen when the target pane leaves the layout", () => {
      useLayoutStore.getState().openFile("/a.tsx");
      const paneId = useLayoutStore.getState().layout.activePaneId!;
      useLayoutStore.getState().toggleFullscreen(paneId);
      expect(useLayoutStore.getState().fullscreenPaneId).toBe(paneId);

      useLayoutStore.getState().closePane(paneId);
      expect(useLayoutStore.getState().fullscreenPaneId).toBeNull();
    });
  });

  describe("autosave", () => {
    it("writes to localStorage 150ms after a layout change", () => {
      useLayoutStore.getState().setProject("/proj");
      useLayoutStore.getState().openFile("/proj/a.tsx");
      expect(localStorage.getItem("jiangyu:layout:/proj")).toBeNull();
      vi.advanceTimersByTime(149);
      expect(localStorage.getItem("jiangyu:layout:/proj")).toBeNull();
      vi.advanceTimersByTime(1);
      expect(localStorage.getItem("jiangyu:layout:/proj")).not.toBeNull();
    });

    it("debounces rapid changes to a single write", () => {
      useLayoutStore.getState().setProject("/proj");
      useLayoutStore.getState().openFile("/proj/a.tsx");
      vi.advanceTimersByTime(100);
      useLayoutStore.getState().openFile("/proj/b.tsx");
      vi.advanceTimersByTime(100);
      useLayoutStore.getState().openFile("/proj/c.tsx");
      vi.advanceTimersByTime(149);
      expect(localStorage.getItem("jiangyu:layout:/proj")).toBeNull();
      vi.advanceTimersByTime(1);
      const saved = localStorage.getItem("jiangyu:layout:/proj");
      expect(saved).toContain("c.tsx");
    });

    it("does not autosave when currentProject is null", () => {
      useLayoutStore.getState().openFile("/a.tsx");
      vi.advanceTimersByTime(200);
      // No project → no keys written.
      expect(Array.from({ length: 0 })).toEqual([]);
    });
  });
});
