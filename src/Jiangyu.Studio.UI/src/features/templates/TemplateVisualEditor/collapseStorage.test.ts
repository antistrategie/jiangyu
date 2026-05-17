import { describe, it, expect, beforeEach, vi } from "vitest";
import type { EditorNode } from "./types";
import {
  computeNodeKeyByUiId,
  loadCollapsed,
  pruneCollapsed,
  saveCollapsed,
} from "./collapseStorage";

function patch(uiId: string, templateType: string, templateId: string): EditorNode {
  return { kind: "Patch", templateType, templateId, directives: [], _uiId: uiId };
}

function clone(uiId: string, templateType: string, cloneId: string): EditorNode {
  return { kind: "Clone", templateType, sourceId: "src", cloneId, directives: [], _uiId: uiId };
}

// In-memory localStorage stub. Each test starts with a fresh store so the
// global key namespace doesn't leak between cases.
function stubLocalStorage(): Map<string, string> {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => {
      store.set(k, v);
    },
    removeItem: (k: string) => {
      store.delete(k);
    },
  });
  return store;
}

beforeEach(() => {
  vi.unstubAllGlobals();
});

describe("computeNodeKeyByUiId", () => {
  it("keys patches by templateType + templateId", () => {
    const nodes = [patch("u1", "Attack", "Soldier_Rifle"), clone("u2", "Effect", "MyEffect")];
    const m = computeNodeKeyByUiId(nodes);
    expect(m.get("u1")).toBe("Patch:Attack:Soldier_Rifle#0");
    expect(m.get("u2")).toBe("Clone:Effect:MyEffect#0");
  });

  it("disambiguates duplicates with an occurrence index", () => {
    const nodes = [
      patch("u1", "Attack", "Soldier_Rifle"),
      patch("u2", "Attack", "Soldier_Rifle"),
      patch("u3", "Attack", "Soldier_Rifle"),
    ];
    const m = computeNodeKeyByUiId(nodes);
    expect(m.get("u1")).toBe("Patch:Attack:Soldier_Rifle#0");
    expect(m.get("u2")).toBe("Patch:Attack:Soldier_Rifle#1");
    expect(m.get("u3")).toBe("Patch:Attack:Soldier_Rifle#2");
  });

  it("treats fresh nodes with empty fields as distinct via occurrence", () => {
    const nodes = [patch("u1", "", ""), patch("u2", "", ""), clone("u3", "", "")];
    const m = computeNodeKeyByUiId(nodes);
    expect(m.get("u1")).toBe("Patch::#0");
    expect(m.get("u2")).toBe("Patch::#1");
    expect(m.get("u3")).toBe("Clone::#0");
  });

  it("regenerated _uiIds map to the same stable key across parses", () => {
    const first = [patch("a1", "Attack", "X"), clone("a2", "Effect", "Y")];
    const second = [patch("b1", "Attack", "X"), clone("b2", "Effect", "Y")];
    const m1 = computeNodeKeyByUiId(first);
    const m2 = computeNodeKeyByUiId(second);
    expect(m1.get("a1")).toBe(m2.get("b1"));
    expect(m1.get("a2")).toBe(m2.get("b2"));
  });
});

describe("loadCollapsed / saveCollapsed", () => {
  it("round-trips a set keyed by file path", () => {
    stubLocalStorage();
    const keys = new Set(["Patch:Attack:Soldier#0", "Clone:Effect:Foo#0"]);
    saveCollapsed("/proj/templates/a.kdl", keys);
    expect(loadCollapsed("/proj/templates/a.kdl")).toEqual(keys);
  });

  it("isolates files by path", () => {
    stubLocalStorage();
    saveCollapsed("/proj/a.kdl", new Set(["Patch:A:1#0"]));
    saveCollapsed("/proj/b.kdl", new Set(["Patch:B:2#0"]));
    expect(loadCollapsed("/proj/a.kdl")).toEqual(new Set(["Patch:A:1#0"]));
    expect(loadCollapsed("/proj/b.kdl")).toEqual(new Set(["Patch:B:2#0"]));
  });

  it("returns an empty set for an unknown file", () => {
    stubLocalStorage();
    expect(loadCollapsed("/never/written.kdl")).toEqual(new Set());
  });

  it("removes the entry when saving an empty set", () => {
    const store = stubLocalStorage();
    saveCollapsed("/proj/a.kdl", new Set(["Patch:A:1#0"]));
    saveCollapsed("/proj/a.kdl", new Set());
    expect([...store.keys()]).toEqual([]);
  });

  it("ignores empty file paths", () => {
    const store = stubLocalStorage();
    saveCollapsed("", new Set(["Patch:A:1#0"]));
    expect([...store.keys()]).toEqual([]);
    expect(loadCollapsed("")).toEqual(new Set());
  });

  it("falls back to an empty set when the stored value is malformed", () => {
    const store = stubLocalStorage();
    store.set("jiangyu:visualEditor:collapsed:/proj/a.kdl", "{not json");
    expect(loadCollapsed("/proj/a.kdl")).toEqual(new Set());
  });

  it("filters out non-string entries from a corrupted array", () => {
    const store = stubLocalStorage();
    store.set(
      "jiangyu:visualEditor:collapsed:/proj/a.kdl",
      JSON.stringify(["Patch:A:1#0", 42, null, "Clone:B:2#0"]),
    );
    expect(loadCollapsed("/proj/a.kdl")).toEqual(new Set(["Patch:A:1#0", "Clone:B:2#0"]));
  });
});

describe("pruneCollapsed", () => {
  it("drops entries that don't match any current node key", () => {
    const stored = new Set(["Patch:A:1#0", "Clone:B:2#0", "Patch:Stale:9#0"]);
    const current = ["Patch:A:1#0", "Clone:B:2#0", "Patch:New:5#0"];
    expect(pruneCollapsed(stored, current)).toEqual(new Set(["Patch:A:1#0", "Clone:B:2#0"]));
  });

  it("returns an empty set when nothing matches", () => {
    const stored = new Set(["Patch:Old:1#0"]);
    const current = ["Patch:New:2#0"];
    expect(pruneCollapsed(stored, current)).toEqual(new Set());
  });
});
