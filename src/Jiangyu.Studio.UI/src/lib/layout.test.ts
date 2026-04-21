import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EMPTY_LAYOUT,
  closePane,
  closeTabs,
  closeTabsEverywhere,
  columnWeight,
  convertPane,
  findPane,
  getActiveCodePane,
  getActivePane,
  getAllOpenPaths,
  getAllPanes,
  loadLayout,
  moveTab,
  movePane,
  movePaneToEdge,
  openFile,
  paneWeight,
  remapPaths,
  saveLayout,
  selectTab,
  setActivePane,
  setColumnWeight,
  setPaneWeight,
  splitDown,
  splitRight,
  splitWithTab,
  type CodePane,
  type Layout,
  type Pane,
} from "./layout.ts";

const A = "/proj/src/A.tsx";
const B = "/proj/src/B.tsx";
const C = "/proj/src/C.tsx";

function asCode(pane: Pane | null | undefined): CodePane {
  if (pane === null || pane === undefined || pane.kind !== "code") {
    throw new Error("expected code pane");
  }
  return pane;
}

function withTwoTabs(): { layout: Layout; paneId: string } {
  const after = openFile(openFile(EMPTY_LAYOUT, A), B);
  return { layout: after, paneId: after.activePaneId! };
}

describe("openFile", () => {
  it("creates the first pane/column when the layout is empty", () => {
    const next = openFile(EMPTY_LAYOUT, A);
    expect(next.columns).toHaveLength(1);
    expect(next.columns[0]!.panes).toHaveLength(1);
    const pane = asCode(next.columns[0]!.panes[0]);
    expect(pane.tabs.map((t) => t.path)).toEqual([A]);
    expect(pane.activeTab).toBe(A);
    expect(next.activePaneId).toBe(pane.id);
  });

  it("adds a tab to the active pane and selects it", () => {
    const { layout, paneId } = withTwoTabs();
    const pane = asCode(findPane(layout, paneId));
    expect(pane.tabs.map((t) => t.path)).toEqual([A, B]);
    expect(pane.activeTab).toBe(B);
  });

  it("re-activates an already-open tab without duplicating it", () => {
    const { layout, paneId } = withTwoTabs();
    const next = openFile(layout, A);
    const pane = asCode(findPane(next, paneId));
    expect(pane.tabs.map((t) => t.path)).toEqual([A, B]);
    expect(pane.activeTab).toBe(A);
  });

  it("opens into an explicitly-named pane and makes it active", () => {
    const first = openFile(EMPTY_LAYOUT, A);
    const split = splitRight(first);
    const newPaneId = split.activePaneId!;
    const reopened = openFile(split, B, first.activePaneId!);
    expect(reopened.activePaneId).toBe(first.activePaneId);
    expect(asCode(findPane(reopened, first.activePaneId!)).tabs.map((t) => t.path)).toEqual([A, B]);
    expect(asCode(findPane(reopened, newPaneId)).tabs).toHaveLength(0);
  });
});

describe("closeTabs", () => {
  it("removes tabs and selects the previous tab when active was closed", () => {
    const { layout, paneId } = withTwoTabs();
    const next = closeTabs(layout, paneId, [B]);
    const pane = asCode(findPane(next, paneId));
    expect(pane.tabs.map((t) => t.path)).toEqual([A]);
    expect(pane.activeTab).toBe(A);
  });

  it("prunes the pane (and column) when the last tab closes", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const next = closeTabs(a, a.activePaneId!, [A]);
    expect(next.columns).toEqual([]);
    expect(next.activePaneId).toBeNull();
  });

  it("focuses the next pane in the same column when the active pane is pruned", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aPaneId = a.activePaneId!;
    const split = splitDown(a);
    const newPaneId = split.activePaneId!;
    const withTab = openFile(split, B);
    const closed = closeTabs(withTab, newPaneId, [B]);
    expect(getActivePane(closed)!.id).toBe(aPaneId);
  });

  it("returns the same layout when the pane is unknown", () => {
    const layout = openFile(EMPTY_LAYOUT, A);
    expect(closeTabs(layout, "missing", [A])).toBe(layout);
  });
});

describe("closeTabsEverywhere", () => {
  it("removes a path from every pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = openFile(splitRight(a), A);
    const closed = closeTabsEverywhere(split, [A]);
    expect(getAllOpenPaths(closed).has(A)).toBe(false);
  });
});

describe("splitRight", () => {
  it("creates a new column to the right of the active pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const next = splitRight(a);
    expect(next.columns).toHaveLength(2);
    expect(next.activePaneId).toBe(next.columns[1]!.panes[0]!.id);
  });

  it("appends a column when there is no active pane", () => {
    const next = splitRight(EMPTY_LAYOUT);
    expect(next.columns).toHaveLength(1);
    expect(next.columns[0]!.panes).toHaveLength(1);
    expect(next.activePaneId).toBe(next.columns[0]!.panes[0]!.id);
  });
});

describe("splitDown", () => {
  it("inserts a pane below the active pane in the same column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const next = splitDown(a);
    expect(next.columns).toHaveLength(1);
    expect(next.columns[0]!.panes).toHaveLength(2);
    expect(next.activePaneId).toBe(next.columns[0]!.panes[1]!.id);
  });

  it("falls back to splitRight when no pane is active", () => {
    const next = splitDown(EMPTY_LAYOUT);
    expect(next.columns).toHaveLength(1);
  });
});

describe("selectTab", () => {
  it("changes active tab and active pane", () => {
    const { layout, paneId } = withTwoTabs();
    const next = selectTab(layout, paneId, A);
    expect(getActiveCodePane(next)!.activeTab).toBe(A);
    expect(next.activePaneId).toBe(paneId);
  });

  it("ignores unknown tabs", () => {
    const { layout, paneId } = withTwoTabs();
    expect(selectTab(layout, paneId, "/missing")).toBe(layout);
  });
});

describe("setActivePane", () => {
  it("activates an existing pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const split = splitRight(a);
    expect(setActivePane(split, aId).activePaneId).toBe(aId);
  });

  it("ignores unknown panes", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(setActivePane(a, "missing")).toBe(a);
  });
});

describe("moveTab", () => {
  it("transfers a tab from source to destination", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const split = splitRight(a);
    const bId = split.activePaneId!;
    const moved = moveTab(split, aId, bId, A);
    expect(findPane(moved, aId)).toBeNull();
    expect(asCode(findPane(moved, bId)).tabs.map((t) => t.path)).toEqual([A]);
  });

  it("is a no-op when source and destination are the same pane", () => {
    const { layout, paneId } = withTwoTabs();
    expect(moveTab(layout, paneId, paneId, A)).toBe(layout);
  });
});

describe("closePane", () => {
  it("removes the pane and its column when it was the only pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const next = closePane(a, a.activePaneId!);
    expect(next.columns).toEqual([]);
    expect(next.activePaneId).toBeNull();
  });

  it("focuses an adjacent column when closing the active pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const split = openFile(splitRight(a), B);
    const closed = closePane(split, split.activePaneId!);
    expect(closed.activePaneId).toBe(aId);
  });
});

describe("remapPaths", () => {
  it("rewrites tab paths and the active tab", () => {
    const { layout } = withTwoTabs();
    const next = remapPaths(layout, (p) => p.replace("/proj/src/", "/proj/lib/"));
    const pane = getActiveCodePane(next)!;
    expect(pane.tabs.map((t) => t.path)).toEqual(["/proj/lib/A.tsx", "/proj/lib/B.tsx"]);
    expect(pane.tabs.map((t) => t.name)).toEqual(["A.tsx", "B.tsx"]);
    expect(pane.activeTab).toBe("/proj/lib/B.tsx");
  });

  it("returns the same layout when no path changes", () => {
    const { layout } = withTwoTabs();
    expect(remapPaths(layout, (p) => p)).toBe(layout);
  });
});

describe("browser panes", () => {
  it("splitRight creates a browser pane when given a browser kind", () => {
    const next = splitRight(EMPTY_LAYOUT, "assetBrowser");
    const pane = next.columns[0]!.panes[0]!;
    expect(pane.kind).toBe("assetBrowser");
    expect(next.activePaneId).toBe(pane.id);
  });

  it("splitDown creates a browser pane when given a browser kind", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const next = splitDown(a, "templateBrowser");
    expect(next.columns[0]!.panes[1]!.kind).toBe("templateBrowser");
  });

  it("openFile skips the browser pane and uses the existing code pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const codePaneId = a.activePaneId!;
    const split = splitRight(a, "assetBrowser");
    expect(getActivePane(split)!.kind).toBe("assetBrowser");
    const opened = openFile(split, B);
    expect(opened.activePaneId).toBe(codePaneId);
    expect(asCode(findPane(opened, codePaneId)).tabs.map((t) => t.path)).toEqual([A, B]);
  });

  it("closeTabs is a no-op on a browser pane", () => {
    const split = splitRight(EMPTY_LAYOUT, "assetBrowser");
    const browserPaneId = split.activePaneId!;
    expect(closeTabs(split, browserPaneId, ["/anything"])).toBe(split);
  });

  it("moveTab refuses to move into a browser pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const codeId = a.activePaneId!;
    const split = splitRight(a, "assetBrowser");
    const browserId = split.activePaneId!;
    expect(moveTab(split, codeId, browserId, A)).toBe(split);
  });

  it("closePane removes a browser pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = splitRight(a, "assetBrowser");
    const browserId = split.activePaneId!;
    const closed = closePane(split, browserId);
    expect(closed.columns).toHaveLength(1);
    expect(getAllPanes(closed).map((p) => p.kind)).toEqual(["code"]);
  });

  it("getAllOpenPaths ignores browser panes", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = splitRight(a, "templateBrowser");
    expect([...getAllOpenPaths(split)]).toEqual([A]);
  });
});

describe("movePane", () => {
  it("moves a pane to a new column on the right of all existing columns", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = openFile(splitRight(a), B);
    const movedId = a.activePaneId!;
    const moved = movePane(split, movedId, { kind: "asNewColumn", index: 2 });
    expect(moved.columns).toHaveLength(2);
    expect(moved.columns[1]!.panes[0]!.id).toBe(movedId);
  });

  it("moves a pane into another column at a given index", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = openFile(splitRight(a), B);
    const downA = splitDown(setActivePane(split, a.activePaneId!));
    const downId = downA.activePaneId!;
    const targetColId = downA.columns[1]!.id;
    const moved = movePane(downA, downId, { kind: "intoColumn", columnId: targetColId, index: 0 });
    expect(moved.columns[0]!.panes).toHaveLength(1);
    expect(moved.columns[1]!.panes[0]!.id).toBe(downId);
  });

  it("is a no-op when the only pane in a column is moved within that same column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const colId = a.columns[0]!.id;
    expect(movePane(a, a.activePaneId!, { kind: "intoColumn", columnId: colId, index: 0 })).toBe(a);
  });

  it("returns the layout unchanged when target column no longer exists after pruning", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const colId = a.columns[0]!.id;
    expect(movePane(a, a.activePaneId!, { kind: "intoColumn", columnId: colId, index: 0 })).toBe(a);
  });
});

describe("convertPane", () => {
  it("replaces a code pane with a browser pane in place", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const paneId = a.activePaneId!;
    const next = convertPane(a, paneId, "assetBrowser");
    expect(findPane(next, paneId)!.kind).toBe("assetBrowser");
    expect(next.columns).toHaveLength(1);
    expect(next.columns[0]!.panes[0]!.id).toBe(paneId);
  });

  it("preserves the pane's weight across the conversion", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const paneId = a.activePaneId!;
    const weighted = setPaneWeight(a, paneId, 3);
    const next = convertPane(weighted, paneId, "templateBrowser");
    expect(paneWeight(findPane(next, paneId)!)).toBe(3);
  });

  it("is a no-op when the pane already has the requested kind", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(convertPane(a, a.activePaneId!, "code")).toBe(a);
  });
});

describe("movePaneToEdge", () => {
  it("places the moved pane as a new column on the right edge", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const split = openFile(splitRight(a), B);
    const bId = split.activePaneId!;
    const moved = movePaneToEdge(split, aId, bId, "right");
    // Source column pruned, so aId is now in the rightmost column.
    expect(moved.columns).toHaveLength(2);
    expect(moved.columns[1]!.panes[0]!.id).toBe(aId);
    expect(moved.columns[0]!.panes[0]!.id).toBe(bId);
  });

  it("places the moved pane above the target inside the same column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const split = openFile(splitRight(a), B);
    const bId = split.activePaneId!;
    const moved = movePaneToEdge(split, aId, bId, "top");
    expect(moved.columns).toHaveLength(1);
    expect(moved.columns[0]!.panes.map((p) => p.id)).toEqual([aId, bId]);
  });

  it("is a no-op when the source equals the target", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(movePaneToEdge(a, a.activePaneId!, a.activePaneId!, "right")).toBe(a);
  });
});

describe("splitWithTab", () => {
  it("creates a new column to the right with the moved tab", () => {
    const { layout, paneId } = withTwoTabs();
    const next = splitWithTab(layout, paneId, paneId, A, "right");
    expect(next.columns).toHaveLength(2);
    expect(asCode(next.columns[1]!.panes[0]).tabs.map((t) => t.path)).toEqual([A]);
    expect(asCode(findPane(next, paneId)).tabs.map((t) => t.path)).toEqual([B]);
  });

  it("creates a new pane below the target with the moved tab", () => {
    const { layout, paneId } = withTwoTabs();
    const next = splitWithTab(layout, paneId, paneId, A, "bottom");
    expect(next.columns).toHaveLength(1);
    expect(next.columns[0]!.panes).toHaveLength(2);
    expect(asCode(next.columns[0]!.panes[1]).tabs.map((t) => t.path)).toEqual([A]);
  });

  it("returns the layout unchanged when the path isn't in the source pane", () => {
    const { layout, paneId } = withTwoTabs();
    expect(splitWithTab(layout, paneId, paneId, "/missing", "right")).toBe(layout);
  });
});

describe("weights", () => {
  it("paneWeight defaults to 1 when not set", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(paneWeight(a.columns[0]!.panes[0]!)).toBe(1);
  });

  it("columnWeight defaults to 1 when not set", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(columnWeight(a.columns[0]!)).toBe(1);
  });

  it("setPaneWeight updates the weight on a single pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const next = setPaneWeight(a, a.activePaneId!, 2.5);
    expect(paneWeight(findPane(next, a.activePaneId!)!)).toBe(2.5);
  });

  it("setColumnWeight updates the weight on a single column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const colId = a.columns[0]!.id;
    const next = setColumnWeight(a, colId, 3);
    expect(columnWeight(next.columns[0]!)).toBe(3);
  });

  it("rejects non-positive weights as a no-op", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(setPaneWeight(a, a.activePaneId!, 0)).toBe(a);
    expect(setColumnWeight(a, a.columns[0]!.id, -1)).toBe(a);
  });

  it("returns the same layout when weight is unchanged", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    expect(setPaneWeight(a, a.activePaneId!, 1)).toBe(a);
  });
});

describe("weight normalisation on insert", () => {
  it("splitRight gives the new column the average of existing column weights", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = openFile(splitRight(a), B);
    const widened = setColumnWeight(split, split.columns[0]!.id, 5);
    // Existing columns: weights 5 and 1 → average 3. Next splitRight should use 3.
    const next = splitRight(widened);
    expect(columnWeight(next.columns[next.columns.length - 1]!)).toBeCloseTo(3);
  });

  it("splitDown gives the new pane the average of existing pane weights in the column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const down = openFile(splitDown(a), B);
    const heavy = setPaneWeight(down, a.activePaneId!, 3);
    // Column panes: weights 3 and 1 → average 2. Next splitDown should use 2.
    const next = splitDown(heavy);
    const column = next.columns[0]!;
    const lastPane = column.panes[column.panes.length - 1]!;
    expect(paneWeight(lastPane)).toBeCloseTo(2);
  });

  it("movePane resets the moved pane's weight to match its new column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const split = openFile(splitRight(a), B);
    const bId = split.activePaneId!;
    const heavy = setPaneWeight(split, aId, 7);
    // Move the heavy pane into the right column; its weight should snap back
    // to the target column's average (1), not stay at 7.
    const moved = movePaneToEdge(heavy, aId, bId, "top");
    const movedPane = findPane(moved, aId)!;
    expect(paneWeight(movedPane)).toBeCloseTo(1);
  });
});

describe("getAllPanes / getAllOpenPaths", () => {
  it("walks every column and pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const split = openFile(splitRight(a), B);
    const splitDownPane = openFile(splitDown(split), C);
    expect(getAllPanes(splitDownPane)).toHaveLength(3);
    expect([...getAllOpenPaths(splitDownPane)].sort()).toEqual([A, B, C].sort());
  });
});

describe("saveLayout / loadLayout", () => {
  beforeEach(() => {
    const store = new Map<string, string>();
    vi.stubGlobal("localStorage", {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => store.set(k, v),
      removeItem: (k: string) => store.delete(k),
      clear: () => store.clear(),
    });
  });

  it("round-trips through localStorage", () => {
    const layout = openFile(splitRight(openFile(EMPTY_LAYOUT, A)), B);
    saveLayout("/proj", layout);
    const loaded = loadLayout("/proj");
    expect(loaded).toEqual(layout);
  });

  it("round-trips browser panes", () => {
    const layout = splitRight(openFile(EMPTY_LAYOUT, A), "templateBrowser");
    saveLayout("/proj", layout);
    expect(loadLayout("/proj")).toEqual(layout);
  });

  it("returns null when nothing saved", () => {
    expect(loadLayout("/missing")).toBeNull();
  });

  it("returns null when stored value is malformed", () => {
    localStorage.setItem("jiangyu:layout:/proj", "{bad json");
    expect(loadLayout("/proj")).toBeNull();
  });
});
