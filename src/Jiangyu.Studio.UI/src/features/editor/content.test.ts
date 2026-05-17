import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the RPC channel before importing the store, since the store calls
// rpcCall() inside load/save/reload. subscribe() is a no-op in these tests —
// we exercise onFileChanged directly rather than fire a fake notification.
vi.mock("@shared/rpc", async (orig) => {
  const actual = await orig<typeof import("@shared/rpc")>();
  return {
    ...actual,
    rpcCall: vi.fn(),
    subscribe: vi.fn(() => () => {}),
  };
});

import { rpcCall } from "@shared/rpc";
import { useEditorContent } from "./content";

const mockRpc = vi.mocked(rpcCall);

function resetStore() {
  useEditorContent.setState({
    contents: {},
    dirty: new Set(),
    conflicts: {},
    editors: {},
    inflight: new Set(),
  });
}

describe("editorContent store", () => {
  beforeEach(() => {
    resetStore();
    mockRpc.mockReset();
  });

  describe("setContent / markDirty", () => {
    it("setContent caches the buffer under the path", () => {
      useEditorContent.getState().setContent("/a", "hello");
      expect(useEditorContent.getState().contents["/a"]).toBe("hello");
    });

    it("setContent is a no-op when text is unchanged", () => {
      useEditorContent.getState().setContent("/a", "hello");
      const before = useEditorContent.getState();
      useEditorContent.getState().setContent("/a", "hello");
      expect(useEditorContent.getState()).toBe(before);
    });

    it("markDirty adds to the dirty set", () => {
      useEditorContent.getState().markDirty("/a", true);
      expect(useEditorContent.getState().dirty.has("/a")).toBe(true);
    });

    it("markDirty(false) removes the entry", () => {
      useEditorContent.getState().markDirty("/a", true);
      useEditorContent.getState().markDirty("/a", false);
      expect(useEditorContent.getState().dirty.has("/a")).toBe(false);
    });

    it("markDirty is a no-op when already in the target state", () => {
      useEditorContent.getState().markDirty("/a", true);
      const before = useEditorContent.getState();
      useEditorContent.getState().markDirty("/a", true);
      expect(useEditorContent.getState()).toBe(before);
    });
  });

  describe("loadContent", () => {
    it("reads the file once and caches the result", async () => {
      mockRpc.mockResolvedValueOnce("contents");
      await useEditorContent.getState().loadContent("/a");
      expect(useEditorContent.getState().contents["/a"]).toBe("contents");
      expect(mockRpc).toHaveBeenCalledWith("readFile", { path: "/a" });
    });

    it("is a no-op when already cached", async () => {
      useEditorContent.getState().setContent("/a", "cached");
      await useEditorContent.getState().loadContent("/a");
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("is a no-op when a read is already in flight for the same path", async () => {
      useEditorContent.setState((s) => ({ inflight: new Set(s.inflight).add("/a") }));
      await useEditorContent.getState().loadContent("/a");
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("clears the inflight entry even when the read throws", async () => {
      mockRpc.mockRejectedValueOnce(new Error("no"));
      await useEditorContent.getState().loadContent("/bad");
      expect(useEditorContent.getState().inflight.has("/bad")).toBe(false);
    });
  });

  describe("save", () => {
    it("writes the cached buffer and clears dirty + conflict", async () => {
      mockRpc.mockResolvedValueOnce(null);
      useEditorContent.getState().setContent("/a", "payload");
      useEditorContent.getState().markDirty("/a", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/a": "changed" } }));
      await useEditorContent.getState().save("/a");
      expect(mockRpc).toHaveBeenCalledWith("writeFile", { path: "/a", content: "payload" });
      expect(useEditorContent.getState().dirty.has("/a")).toBe(false);
      expect(useEditorContent.getState().conflicts["/a"]).toBeUndefined();
    });

    it("is a no-op when no buffer has been loaded for the path", async () => {
      await useEditorContent.getState().save("/never-loaded");
      expect(mockRpc).not.toHaveBeenCalled();
    });
  });

  describe("reload", () => {
    it("replaces the buffer and clears dirty + conflict", async () => {
      mockRpc.mockResolvedValueOnce("fresh");
      useEditorContent.getState().setContent("/a", "stale");
      useEditorContent.getState().markDirty("/a", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/a": "changed" } }));
      await useEditorContent.getState().reload("/a");
      expect(useEditorContent.getState().contents["/a"]).toBe("fresh");
      expect(useEditorContent.getState().dirty.has("/a")).toBe(false);
      expect(useEditorContent.getState().conflicts["/a"]).toBeUndefined();
    });
  });

  describe("dismissConflict", () => {
    it("drops the conflict entry", () => {
      useEditorContent.setState({ conflicts: { "/a": "changed" } });
      useEditorContent.getState().dismissConflict("/a");
      expect(useEditorContent.getState().conflicts["/a"]).toBeUndefined();
    });

    it("is a no-op when no conflict is set", () => {
      const before = useEditorContent.getState();
      useEditorContent.getState().dismissConflict("/a");
      expect(useEditorContent.getState()).toBe(before);
    });
  });

  describe("appendToFile", () => {
    // Append always separates with a blank line — one newline when the
    // cached buffer already ends with one, two otherwise.
    it("appends to an already-cached buffer without reading the file", async () => {
      useEditorContent.getState().setContent("/a", "line1\n");
      await useEditorContent.getState().appendToFile("/a", "line2");
      expect(useEditorContent.getState().contents["/a"]).toBe("line1\n\nline2");
      expect(useEditorContent.getState().dirty.has("/a")).toBe(true);
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("inserts a double newline when the cached buffer doesn't end with one", async () => {
      useEditorContent.getState().setContent("/a", "line1");
      await useEditorContent.getState().appendToFile("/a", "line2");
      expect(useEditorContent.getState().contents["/a"]).toBe("line1\n\nline2");
    });

    it("reads the file first when no buffer is cached", async () => {
      mockRpc.mockResolvedValueOnce("disk\n");
      await useEditorContent.getState().appendToFile("/a", "append");
      expect(useEditorContent.getState().contents["/a"]).toBe("disk\n\nappend");
      expect(useEditorContent.getState().dirty.has("/a")).toBe(true);
    });

    it("treats a missing file as an empty buffer", async () => {
      mockRpc.mockRejectedValueOnce(new Error("enoent"));
      await useEditorContent.getState().appendToFile("/new", "hello");
      expect(useEditorContent.getState().contents["/new"]).toBe("hello");
    });
  });

  describe("onFileChanged", () => {
    it("is a no-op for a path we don't track", () => {
      const before = useEditorContent.getState();
      useEditorContent.getState().onFileChanged({ path: "/unknown", kind: "changed" });
      expect(useEditorContent.getState()).toBe(before);
    });

    it("sets a 'deleted' conflict for tracked paths", () => {
      useEditorContent.getState().setContent("/a", "x");
      useEditorContent.getState().onFileChanged({ path: "/a", kind: "deleted" });
      expect(useEditorContent.getState().conflicts["/a"]).toBe("deleted");
    });

    it("sets a 'changed' conflict when the buffer is dirty", () => {
      useEditorContent.getState().setContent("/a", "x");
      useEditorContent.getState().markDirty("/a", true);
      useEditorContent.getState().onFileChanged({ path: "/a", kind: "changed" });
      expect(useEditorContent.getState().conflicts["/a"]).toBe("changed");
    });

    it("silently reloads a clean buffer", async () => {
      mockRpc.mockResolvedValueOnce("updated");
      useEditorContent.getState().setContent("/a", "stale");
      useEditorContent.getState().onFileChanged({ path: "/a", kind: "changed" });
      // Silent reload is fire-and-forget; wait for the microtask to resolve.
      await new Promise((r) => setTimeout(r, 0));
      expect(useEditorContent.getState().contents["/a"]).toBe("updated");
      expect(useEditorContent.getState().conflicts["/a"]).toBeUndefined();
    });
  });

  describe("remapPath", () => {
    it("moves content + conflict + dirty under the new key", () => {
      useEditorContent.getState().setContent("/old", "text");
      useEditorContent.getState().markDirty("/old", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/old": "changed" } }));

      useEditorContent.getState().remapPath("/old", "/new");

      const s = useEditorContent.getState();
      expect(s.contents["/old"]).toBeUndefined();
      expect(s.contents["/new"]).toBe("text");
      expect(s.dirty.has("/old")).toBe(false);
      expect(s.dirty.has("/new")).toBe(true);
      expect(s.conflicts["/old"]).toBeUndefined();
      expect(s.conflicts["/new"]).toBe("changed");
    });

    it("is a no-op when old and new paths are the same", () => {
      useEditorContent.getState().setContent("/a", "x");
      const before = useEditorContent.getState();
      useEditorContent.getState().remapPath("/a", "/a");
      expect(useEditorContent.getState()).toBe(before);
    });

    it("is a no-op when the old path isn't tracked", () => {
      const before = useEditorContent.getState();
      useEditorContent.getState().remapPath("/ghost", "/new");
      expect(useEditorContent.getState()).toBe(before);
    });
  });

  describe("prune", () => {
    it("drops content for paths not in openPaths", () => {
      useEditorContent.getState().setContent("/keep", "k");
      useEditorContent.getState().setContent("/drop", "d");
      useEditorContent.getState().prune(new Set(["/keep"]), new Set());
      const s = useEditorContent.getState();
      expect(s.contents["/keep"]).toBe("k");
      expect(s.contents["/drop"]).toBeUndefined();
    });

    it("drops dirty + conflict entries for paths no longer open", () => {
      useEditorContent.getState().setContent("/drop", "d");
      useEditorContent.getState().markDirty("/drop", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/drop": "changed" } }));
      useEditorContent.getState().prune(new Set(), new Set());
      const s = useEditorContent.getState();
      expect(s.dirty.has("/drop")).toBe(false);
      expect(s.conflicts["/drop"]).toBeUndefined();
    });

    it("is a no-op when everything is still in scope", () => {
      useEditorContent.getState().setContent("/keep", "k");
      const before = useEditorContent.getState();
      useEditorContent.getState().prune(new Set(["/keep"]), new Set());
      expect(useEditorContent.getState()).toBe(before);
    });
  });
});
