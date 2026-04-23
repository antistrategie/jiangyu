import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the RPC channel before importing the store, since the store calls
// rpcCall() inside load/save/reload. subscribe() is a no-op in these tests —
// we exercise onFileChanged directly rather than fire a fake notification.
vi.mock("@lib/rpc.ts", async (orig) => {
  const actual = await orig<typeof import("@lib/rpc.ts")>();
  return {
    ...actual,
    rpcCall: vi.fn(),
    subscribe: vi.fn(() => () => {}),
  };
});

import { rpcCall } from "@lib/rpc.ts";
import { useEditorContent } from "./content.ts";

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
      useEditorContent.getState().setContent("/a.ts", "hello");
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("hello");
    });

    it("setContent is a no-op when text is unchanged", () => {
      useEditorContent.getState().setContent("/a.ts", "hello");
      const before = useEditorContent.getState();
      useEditorContent.getState().setContent("/a.ts", "hello");
      expect(useEditorContent.getState()).toBe(before);
    });

    it("markDirty adds to the dirty set", () => {
      useEditorContent.getState().markDirty("/a.ts", true);
      expect(useEditorContent.getState().dirty.has("/a.ts")).toBe(true);
    });

    it("markDirty(false) removes the entry", () => {
      useEditorContent.getState().markDirty("/a.ts", true);
      useEditorContent.getState().markDirty("/a.ts", false);
      expect(useEditorContent.getState().dirty.has("/a.ts")).toBe(false);
    });

    it("markDirty is a no-op when already in the target state", () => {
      useEditorContent.getState().markDirty("/a.ts", true);
      const before = useEditorContent.getState();
      useEditorContent.getState().markDirty("/a.ts", true);
      expect(useEditorContent.getState()).toBe(before);
    });
  });

  describe("loadContent", () => {
    it("reads the file once and caches the result", async () => {
      mockRpc.mockResolvedValueOnce("contents");
      await useEditorContent.getState().loadContent("/a.ts");
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("contents");
      expect(mockRpc).toHaveBeenCalledWith("readFile", { path: "/a.ts" });
    });

    it("is a no-op when already cached", async () => {
      useEditorContent.getState().setContent("/a.ts", "cached");
      await useEditorContent.getState().loadContent("/a.ts");
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("is a no-op when a read is already in flight for the same path", async () => {
      useEditorContent.setState((s) => ({ inflight: new Set(s.inflight).add("/a.ts") }));
      await useEditorContent.getState().loadContent("/a.ts");
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("clears the inflight entry even when the read throws", async () => {
      mockRpc.mockRejectedValueOnce(new Error("no"));
      await useEditorContent.getState().loadContent("/bad.ts");
      expect(useEditorContent.getState().inflight.has("/bad.ts")).toBe(false);
    });
  });

  describe("save", () => {
    it("writes the cached buffer and clears dirty + conflict", async () => {
      mockRpc.mockResolvedValueOnce(null);
      useEditorContent.getState().setContent("/a.ts", "payload");
      useEditorContent.getState().markDirty("/a.ts", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/a.ts": "changed" } }));
      await useEditorContent.getState().save("/a.ts");
      expect(mockRpc).toHaveBeenCalledWith("writeFile", { path: "/a.ts", content: "payload" });
      expect(useEditorContent.getState().dirty.has("/a.ts")).toBe(false);
      expect(useEditorContent.getState().conflicts["/a.ts"]).toBeUndefined();
    });

    it("is a no-op when no buffer has been loaded for the path", async () => {
      await useEditorContent.getState().save("/never-loaded.ts");
      expect(mockRpc).not.toHaveBeenCalled();
    });
  });

  describe("reload", () => {
    it("replaces the buffer and clears dirty + conflict", async () => {
      mockRpc.mockResolvedValueOnce("fresh");
      useEditorContent.getState().setContent("/a.ts", "stale");
      useEditorContent.getState().markDirty("/a.ts", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/a.ts": "changed" } }));
      await useEditorContent.getState().reload("/a.ts");
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("fresh");
      expect(useEditorContent.getState().dirty.has("/a.ts")).toBe(false);
      expect(useEditorContent.getState().conflicts["/a.ts"]).toBeUndefined();
    });
  });

  describe("dismissConflict", () => {
    it("drops the conflict entry", () => {
      useEditorContent.setState({ conflicts: { "/a.ts": "changed" } });
      useEditorContent.getState().dismissConflict("/a.ts");
      expect(useEditorContent.getState().conflicts["/a.ts"]).toBeUndefined();
    });

    it("is a no-op when no conflict is set", () => {
      const before = useEditorContent.getState();
      useEditorContent.getState().dismissConflict("/a.ts");
      expect(useEditorContent.getState()).toBe(before);
    });
  });

  describe("appendToFile", () => {
    // Append always separates with a blank line — one newline when the
    // cached buffer already ends with one, two otherwise.
    it("appends to an already-cached buffer without reading the file", async () => {
      useEditorContent.getState().setContent("/a.ts", "line1\n");
      await useEditorContent.getState().appendToFile("/a.ts", "line2");
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("line1\n\nline2");
      expect(useEditorContent.getState().dirty.has("/a.ts")).toBe(true);
      expect(mockRpc).not.toHaveBeenCalled();
    });

    it("inserts a double newline when the cached buffer doesn't end with one", async () => {
      useEditorContent.getState().setContent("/a.ts", "line1");
      await useEditorContent.getState().appendToFile("/a.ts", "line2");
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("line1\n\nline2");
    });

    it("reads the file first when no buffer is cached", async () => {
      mockRpc.mockResolvedValueOnce("disk\n");
      await useEditorContent.getState().appendToFile("/a.ts", "append");
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("disk\n\nappend");
      expect(useEditorContent.getState().dirty.has("/a.ts")).toBe(true);
    });

    it("treats a missing file as an empty buffer", async () => {
      mockRpc.mockRejectedValueOnce(new Error("enoent"));
      await useEditorContent.getState().appendToFile("/new.ts", "hello");
      expect(useEditorContent.getState().contents["/new.ts"]).toBe("hello");
    });
  });

  describe("onFileChanged", () => {
    it("is a no-op for a path we don't track", () => {
      const before = useEditorContent.getState();
      useEditorContent.getState().onFileChanged({ path: "/unknown.ts", kind: "changed" });
      expect(useEditorContent.getState()).toBe(before);
    });

    it("sets a 'deleted' conflict for tracked paths", () => {
      useEditorContent.getState().setContent("/a.ts", "x");
      useEditorContent.getState().onFileChanged({ path: "/a.ts", kind: "deleted" });
      expect(useEditorContent.getState().conflicts["/a.ts"]).toBe("deleted");
    });

    it("sets a 'changed' conflict when the buffer is dirty", () => {
      useEditorContent.getState().setContent("/a.ts", "x");
      useEditorContent.getState().markDirty("/a.ts", true);
      useEditorContent.getState().onFileChanged({ path: "/a.ts", kind: "changed" });
      expect(useEditorContent.getState().conflicts["/a.ts"]).toBe("changed");
    });

    it("silently reloads a clean buffer", async () => {
      mockRpc.mockResolvedValueOnce("updated");
      useEditorContent.getState().setContent("/a.ts", "stale");
      useEditorContent.getState().onFileChanged({ path: "/a.ts", kind: "changed" });
      // Silent reload is fire-and-forget; wait for the microtask to resolve.
      await new Promise((r) => setTimeout(r, 0));
      expect(useEditorContent.getState().contents["/a.ts"]).toBe("updated");
      expect(useEditorContent.getState().conflicts["/a.ts"]).toBeUndefined();
    });
  });

  describe("remapPath", () => {
    it("moves content + conflict + dirty under the new key", () => {
      useEditorContent.getState().setContent("/old.ts", "text");
      useEditorContent.getState().markDirty("/old.ts", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/old.ts": "changed" } }));

      useEditorContent.getState().remapPath("/old.ts", "/new.ts");

      const s = useEditorContent.getState();
      expect(s.contents["/old.ts"]).toBeUndefined();
      expect(s.contents["/new.ts"]).toBe("text");
      expect(s.dirty.has("/old.ts")).toBe(false);
      expect(s.dirty.has("/new.ts")).toBe(true);
      expect(s.conflicts["/old.ts"]).toBeUndefined();
      expect(s.conflicts["/new.ts"]).toBe("changed");
    });

    it("is a no-op when old and new paths are the same", () => {
      useEditorContent.getState().setContent("/a.ts", "x");
      const before = useEditorContent.getState();
      useEditorContent.getState().remapPath("/a.ts", "/a.ts");
      expect(useEditorContent.getState()).toBe(before);
    });

    it("is a no-op when the old path isn't tracked", () => {
      const before = useEditorContent.getState();
      useEditorContent.getState().remapPath("/ghost.ts", "/new.ts");
      expect(useEditorContent.getState()).toBe(before);
    });
  });

  describe("prune", () => {
    it("drops content for paths not in openPaths", () => {
      useEditorContent.getState().setContent("/keep.ts", "k");
      useEditorContent.getState().setContent("/drop.ts", "d");
      useEditorContent.getState().prune(new Set(["/keep.ts"]), new Set());
      const s = useEditorContent.getState();
      expect(s.contents["/keep.ts"]).toBe("k");
      expect(s.contents["/drop.ts"]).toBeUndefined();
    });

    it("drops dirty + conflict entries for paths no longer open", () => {
      useEditorContent.getState().setContent("/drop.ts", "d");
      useEditorContent.getState().markDirty("/drop.ts", true);
      useEditorContent.setState((s) => ({ conflicts: { ...s.conflicts, "/drop.ts": "changed" } }));
      useEditorContent.getState().prune(new Set(), new Set());
      const s = useEditorContent.getState();
      expect(s.dirty.has("/drop.ts")).toBe(false);
      expect(s.conflicts["/drop.ts"]).toBeUndefined();
    });

    it("is a no-op when everything is still in scope", () => {
      useEditorContent.getState().setContent("/keep.ts", "k");
      const before = useEditorContent.getState();
      useEditorContent.getState().prune(new Set(["/keep.ts"]), new Set());
      expect(useEditorContent.getState()).toBe(before);
    });
  });
});
