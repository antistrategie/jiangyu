import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  clearRecentProjects,
  loadRecentProjects,
  recordRecentProject,
  removeRecentProject,
} from "@features/project/recent";

beforeEach(() => {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => store.set(k, v),
    removeItem: (k: string) => store.delete(k),
    clear: () => store.clear(),
  });
});

describe("loadRecentProjects", () => {
  it("returns an empty array when nothing is stored", () => {
    expect(loadRecentProjects()).toEqual([]);
  });

  it("returns an empty array when stored value is malformed", () => {
    localStorage.setItem("jiangyu:recentProjects", "{not json");
    expect(loadRecentProjects()).toEqual([]);
  });

  it("filters out non-string entries defensively", () => {
    localStorage.setItem("jiangyu:recentProjects", JSON.stringify(["/a", 1, null, "/b"]));
    expect(loadRecentProjects()).toEqual(["/a", "/b"]);
  });
});

describe("recordRecentProject", () => {
  it("inserts a new path at the front", () => {
    expect(recordRecentProject("/proj/a")).toEqual(["/proj/a"]);
    expect(recordRecentProject("/proj/b")).toEqual(["/proj/b", "/proj/a"]);
  });

  it("moves an existing path to the front instead of duplicating", () => {
    recordRecentProject("/proj/a");
    recordRecentProject("/proj/b");
    expect(recordRecentProject("/proj/a")).toEqual(["/proj/a", "/proj/b"]);
  });

  it("caps the list at MAX_ENTRIES (10)", () => {
    for (let i = 0; i < 15; i++) recordRecentProject(`/proj/${i}`);
    const list = loadRecentProjects();
    expect(list).toHaveLength(10);
    expect(list[0]).toBe("/proj/14");
    expect(list[9]).toBe("/proj/5");
  });
});

describe("removeRecentProject", () => {
  it("removes the matching entry", () => {
    recordRecentProject("/proj/a");
    recordRecentProject("/proj/b");
    expect(removeRecentProject("/proj/a")).toEqual(["/proj/b"]);
  });

  it("is a no-op when the entry isn't present", () => {
    recordRecentProject("/proj/a");
    expect(removeRecentProject("/proj/missing")).toEqual(["/proj/a"]);
  });
});

describe("clearRecentProjects", () => {
  it("empties the list", () => {
    recordRecentProject("/proj/a");
    recordRecentProject("/proj/b");
    clearRecentProjects();
    expect(loadRecentProjects()).toEqual([]);
  });
});
