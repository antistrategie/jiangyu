import { describe, it, expect, beforeEach, vi } from "vitest";
import type { EditorDirective, EditorNode, EditorValue } from "./types";
import {
  computeCompositeKeyByUiId,
  computeNodeKeyByUiId,
  loadCollapsed,
  loadCompositeCollapse,
  pruneCollapsed,
  pruneCompositeCollapse,
  saveCollapsed,
  saveCompositeCollapse,
} from "./collapseStorage";

function patch(uiId: string, templateType: string, templateId: string): EditorNode {
  return { kind: "Patch", templateType, templateId, directives: [], _uiId: uiId };
}

function clone(uiId: string, templateType: string, cloneId: string): EditorNode {
  return { kind: "Clone", templateType, sourceId: "src", cloneId, directives: [], _uiId: uiId };
}

function create(uiId: string, templateType: string, cloneId: string): EditorNode {
  return { kind: "Create", templateType, cloneId, directives: [], _uiId: uiId };
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

  it("keys creates by their new id (cloneId), distinct from a clone of the same id", () => {
    const nodes = [create("u1", "SoundBank", "my_bank"), clone("u2", "SoundBank", "my_bank")];
    const m = computeNodeKeyByUiId(nodes);
    expect(m.get("u1")).toBe("Create:SoundBank:my_bank#0");
    expect(m.get("u2")).toBe("Clone:SoundBank:my_bank#0");
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

function composite(compositeType: string, inner: EditorDirective[] = []): EditorValue {
  return { kind: "Composite", compositeType, compositeDirectives: inner };
}

function set(uiId: string, fieldPath: string, value: EditorValue): EditorDirective {
  return { op: "Set", fieldPath, value, _uiId: uiId };
}

function appendDir(uiId: string, fieldPath: string, value: EditorValue): EditorDirective {
  return { op: "Append", fieldPath, value, _uiId: uiId };
}

describe("computeCompositeKeyByUiId", () => {
  it("returns an empty map when no directives carry composite values", () => {
    const nodes: EditorNode[] = [
      {
        kind: "Patch",
        templateType: "Attack",
        templateId: "x",
        _uiId: "n1",
        directives: [
          { op: "Set", fieldPath: "Damage", value: { kind: "Int32", int32: 5 }, _uiId: "d1" },
        ],
      },
    ];
    const nodeKeys = computeNodeKeyByUiId(nodes);
    expect(computeCompositeKeyByUiId(nodes, nodeKeys).size).toBe(0);
  });

  it("assigns positional path keys for top-level composites", () => {
    const nodes: EditorNode[] = [
      {
        kind: "Clone",
        templateType: "SoundBank",
        sourceId: "src",
        cloneId: "voymastina_va",
        _uiId: "n1",
        directives: [
          appendDir("d0", "sounds", composite("Sound")),
          appendDir("d1", "sounds", composite("Sound")),
        ],
      },
    ];
    const nodeKeys = computeNodeKeyByUiId(nodes);
    const compositeKeys = computeCompositeKeyByUiId(nodes, nodeKeys);
    const nodeKey = nodeKeys.get("n1");
    expect(compositeKeys.get("d0")).toBe(`${nodeKey}/dir[0]`);
    expect(compositeKeys.get("d1")).toBe(`${nodeKey}/dir[1]`);
  });

  it("recurses into nested composite directives", () => {
    const innerComposite = composite("SoundVariation", [
      set("inner1", "clip", { kind: "AssetReference", assetName: "clip.wav" }),
    ]);
    const outerComposite = composite("Sound", [
      appendDir("variations0", "variations", innerComposite),
    ]);
    const nodes: EditorNode[] = [
      {
        kind: "Clone",
        templateType: "SoundBank",
        sourceId: "src",
        cloneId: "voymastina_va",
        _uiId: "n1",
        directives: [appendDir("sounds0", "sounds", outerComposite)],
      },
    ];
    const nodeKeys = computeNodeKeyByUiId(nodes);
    const compositeKeys = computeCompositeKeyByUiId(nodes, nodeKeys);
    const nodeKey = nodeKeys.get("n1");
    expect(compositeKeys.get("sounds0")).toBe(`${nodeKey}/dir[0]`);
    expect(compositeKeys.get("variations0")).toBe(`${nodeKey}/dir[0]/dir[0]`);
    // Scalar inner directive isn't a composite — no entry.
    expect(compositeKeys.has("inner1")).toBe(false);
  });

  it("regenerated _uiIds map to the same key on the next parse", () => {
    const first: EditorNode[] = [
      {
        kind: "Clone",
        templateType: "SoundBank",
        sourceId: "src",
        cloneId: "vox",
        _uiId: "a1",
        directives: [appendDir("a-d0", "sounds", composite("Sound"))],
      },
    ];
    const second: EditorNode[] = [
      {
        kind: "Clone",
        templateType: "SoundBank",
        sourceId: "src",
        cloneId: "vox",
        _uiId: "b1",
        directives: [appendDir("b-d0", "sounds", composite("Sound"))],
      },
    ];
    const c1 = computeCompositeKeyByUiId(first, computeNodeKeyByUiId(first));
    const c2 = computeCompositeKeyByUiId(second, computeNodeKeyByUiId(second));
    expect(c1.get("a-d0")).toBe(c2.get("b-d0"));
  });
});

describe("loadCompositeCollapse / saveCompositeCollapse", () => {
  it("round-trips a Map keyed by file path", () => {
    stubLocalStorage();
    const map = new Map<string, boolean>([
      ["Clone:SoundBank:vox#0/dir[0]", true],
      ["Clone:SoundBank:vox#0/dir[1]", false],
    ]);
    saveCompositeCollapse("/proj/templates/a.kdl", map);
    expect(loadCompositeCollapse("/proj/templates/a.kdl")).toEqual(map);
  });

  it("isolates files by path and avoids the node-collapse namespace", () => {
    const store = stubLocalStorage();
    saveCollapsed("/proj/a.kdl", new Set(["Patch:A:1#0"]));
    saveCompositeCollapse("/proj/a.kdl", new Map([["Patch:A:1#0/dir[0]", true]]));
    expect(loadCollapsed("/proj/a.kdl")).toEqual(new Set(["Patch:A:1#0"]));
    expect(loadCompositeCollapse("/proj/a.kdl")).toEqual(new Map([["Patch:A:1#0/dir[0]", true]]));
    // Two distinct storage keys so the namespaces don't collide.
    const keys = [...store.keys()].sort();
    expect(keys).toEqual([
      "jiangyu:visualEditor:collapsed:/proj/a.kdl",
      "jiangyu:visualEditor:compositeCollapsed:/proj/a.kdl",
    ]);
  });

  it("returns an empty map for an unknown file", () => {
    stubLocalStorage();
    expect(loadCompositeCollapse("/never/written.kdl")).toEqual(new Map());
  });

  it("removes the entry when saving an empty map", () => {
    const store = stubLocalStorage();
    saveCompositeCollapse("/proj/a.kdl", new Map([["k", true]]));
    saveCompositeCollapse("/proj/a.kdl", new Map());
    expect([...store.keys()]).toEqual([]);
  });

  it("ignores empty file paths", () => {
    const store = stubLocalStorage();
    saveCompositeCollapse("", new Map([["k", true]]));
    expect([...store.keys()]).toEqual([]);
    expect(loadCompositeCollapse("")).toEqual(new Map());
  });

  it("falls back to an empty map when the stored value is malformed", () => {
    const store = stubLocalStorage();
    store.set("jiangyu:visualEditor:compositeCollapsed:/proj/a.kdl", "{not json");
    expect(loadCompositeCollapse("/proj/a.kdl")).toEqual(new Map());
  });

  it("filters out non-pair entries from a corrupted array", () => {
    const store = stubLocalStorage();
    store.set(
      "jiangyu:visualEditor:compositeCollapsed:/proj/a.kdl",
      JSON.stringify([
        ["good", true],
        ["bad-shape"],
        ["wrong-type", "string-not-bool"],
        ["also-good", false],
      ]),
    );
    expect(loadCompositeCollapse("/proj/a.kdl")).toEqual(
      new Map([
        ["good", true],
        ["also-good", false],
      ]),
    );
  });
});

describe("pruneCompositeCollapse", () => {
  it("drops entries whose keys don't appear in the current set", () => {
    const stored = new Map<string, boolean>([
      ["live#0/dir[0]", true],
      ["stale#0/dir[7]", false],
    ]);
    const pruned = pruneCompositeCollapse(stored, ["live#0/dir[0]", "live#0/dir[1]"]);
    expect(pruned).toEqual(new Map([["live#0/dir[0]", true]]));
  });

  it("preserves the explicit state of surviving entries", () => {
    const stored = new Map<string, boolean>([
      ["k1", true],
      ["k2", false],
    ]);
    expect(pruneCompositeCollapse(stored, ["k1", "k2"])).toEqual(stored);
  });
});
