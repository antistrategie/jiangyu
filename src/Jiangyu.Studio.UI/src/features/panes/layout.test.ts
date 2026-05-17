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
  insertCrossWindowPane,
  insertPaneAtEdge,
  loadLayout,
  moveTab,
  movePane,
  movePaneToEdge,
  openFile,
  paneWeight,
  remapPaths,
  reorderTab,
  saveLayout,
  selectTab,
  setActivePane,
  setBrowserPaneState,
  setColumnWeight,
  setPaneWeight,
  splitAtEdgeWithPath,
  splitDown,
  splitRight,
  splitWithTab,
  swapPanes,
  type BrowserPane,
  type CodePane,
  type Layout,
  type Pane,
} from "./layout";
import {
  DEFAULT_ASSET_BROWSER_STATE,
  DEFAULT_TEMPLATE_BROWSER_STATE,
} from "@features/panes/browserState";

const A = "/proj/src/A";
const B = "/proj/src/B";
const C = "/proj/src/C";

function asCode(pane: Pane | null | undefined): CodePane {
  if (pane?.kind !== "code") {
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
    const reopened = openFile(split, B, first.activePaneId);
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

describe("closePane weight redistribution", () => {
  it("conserves total pane weight when removing a pane from a multi-pane column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const down = openFile(splitDown(a), B);
    const bId = down.activePaneId!;
    const weighted = setPaneWeight(setPaneWeight(down, aId, 1), bId, 3);
    const totalBefore = weighted.columns[0]!.panes.reduce((s, p) => s + paneWeight(p), 0);

    const closed = closePane(weighted, aId);
    const totalAfter = closed.columns[0]!.panes.reduce((s, p) => s + paneWeight(p), 0);
    expect(totalAfter).toBeCloseTo(totalBefore);
  });

  it("preserves ratio between surviving panes in a 3-pane column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const down1 = openFile(splitDown(a), B);
    const bId = down1.activePaneId!;
    const down2 = openFile(splitDown(down1), C);
    const cId = down2.activePaneId!;
    const weighted = setPaneWeight(setPaneWeight(setPaneWeight(down2, aId, 2), bId, 3), cId, 5);
    const ratioBefore = paneWeight(findPane(weighted, bId)!) / paneWeight(findPane(weighted, cId)!);

    const closed = closePane(weighted, aId);
    const ratioAfter = paneWeight(findPane(closed, bId)!) / paneWeight(findPane(closed, cId)!);
    expect(ratioAfter).toBeCloseTo(ratioBefore);
  });

  it("does not change column weights when a pane is removed from a multi-pane column", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const right = openFile(splitRight(a), B);
    const down = openFile(splitDown(setActivePane(right, aId)), C);
    const col0Id = down.columns[0]!.id;
    const col1Id = down.columns[1]!.id;
    const weighted = setColumnWeight(setColumnWeight(down, col0Id, 2), col1Id, 5);
    const cId = down.activePaneId!;

    const closed = closePane(weighted, cId);
    const col0 = closed.columns.find((c) => c.id === col0Id)!;
    const col1 = closed.columns.find((c) => c.id === col1Id)!;
    expect(columnWeight(col0)).toBe(2);
    expect(columnWeight(col1)).toBe(5);
  });

  it("conserves total column weight when removing the sole pane (column removal)", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const right = openFile(splitRight(a), B);
    const right2 = openFile(splitRight(right), C);
    const col0 = right2.columns[0]!;
    const col1 = right2.columns[1]!;
    const col2 = right2.columns[2]!;
    const weighted = setColumnWeight(
      setColumnWeight(setColumnWeight(right2, col0.id, 2), col1.id, 3),
      col2.id,
      5,
    );
    const totalBefore = weighted.columns.reduce((s, c) => s + columnWeight(c), 0);

    const bPaneId = weighted.columns[1]!.panes[0]!.id;
    const closed = closePane(weighted, bPaneId);
    const totalAfter = closed.columns.reduce((s, c) => s + columnWeight(c), 0);
    expect(totalAfter).toBeCloseTo(totalBefore);
  });

  it("preserves ratio between surviving columns when a column is removed", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const right = openFile(splitRight(a), B);
    const right2 = openFile(splitRight(right), C);
    const col0 = right2.columns[0]!;
    const col2 = right2.columns[2]!;
    const weighted = setColumnWeight(
      setColumnWeight(setColumnWeight(right2, col0.id, 2), right2.columns[1]!.id, 3),
      col2.id,
      5,
    );
    const ratioBefore = columnWeight(weighted.columns[0]!) / columnWeight(weighted.columns[2]!);

    const bPaneId = weighted.columns[1]!.panes[0]!.id;
    const closed = closePane(weighted, bPaneId);
    const ratioAfter = columnWeight(closed.columns[0]!) / columnWeight(closed.columns[1]!);
    expect(ratioAfter).toBeCloseTo(ratioBefore);
  });

  it("does not change pane weights in unrelated columns when a column is removed", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const right = openFile(splitRight(a), B);
    const bId = right.activePaneId!;
    const weighted = setPaneWeight(setPaneWeight(right, aId, 7), bId, 3);

    const closed = closePane(weighted, bId);
    expect(paneWeight(findPane(closed, aId)!)).toBe(7);
  });

  it("works correctly with default weights (all 1)", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const down = openFile(splitDown(a), B);
    const bId = down.activePaneId!;

    const closed = closePane(down, aId);
    // Sole survivor absorbs the removed pane's weight: 1 + 1 = 2.
    expect(paneWeight(findPane(closed, bId)!)).toBeCloseTo(2);
  });

  it("keeps activePaneId unchanged when closing a non-active pane", () => {
    const a = openFile(EMPTY_LAYOUT, A);
    const aId = a.activePaneId!;
    const down = openFile(splitDown(a), B);
    const bId = down.activePaneId!;
    const reactivated = setActivePane(down, aId);

    const closed = closePane(reactivated, bId);
    expect(closed.activePaneId).toBe(aId);
  });
});

describe("remapPaths", () => {
  it("rewrites tab paths and the active tab", () => {
    const { layout } = withTwoTabs();
    const next = remapPaths(layout, (p) => p.replace("/proj/src/", "/proj/lib/"));
    const pane = getActiveCodePane(next)!;
    expect(pane.tabs.map((t) => t.path)).toEqual(["/proj/lib/A", "/proj/lib/B"]);
    expect(pane.tabs.map((t) => t.name)).toEqual(["A", "B"]);
    expect(pane.activeTab).toBe("/proj/lib/B");
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

describe("swapPanes", () => {
  it("exchanges the contents of two panes in different columns", () => {
    const l1 = openFile(EMPTY_LAYOUT, A);
    const l2 = splitRight(l1);
    const l3 = openFile(l2, B);
    const [leftBefore, rightBefore] = getAllPanes(l3);
    expect(leftBefore!.id).not.toBe(rightBefore!.id);
    expect((leftBefore as CodePane).tabs.map((t) => t.path)).toEqual([A]);
    expect((rightBefore as CodePane).tabs.map((t) => t.path)).toEqual([B]);

    const swapped = swapPanes(l3, leftBefore!.id, rightBefore!.id);
    const [leftAfter, rightAfter] = getAllPanes(swapped);
    // Position 0 now holds the pane that used to be at position 1.
    expect(leftAfter!.id).toBe(rightBefore!.id);
    expect((leftAfter as CodePane).tabs.map((t) => t.path)).toEqual([B]);
    expect(rightAfter!.id).toBe(leftBefore!.id);
    expect((rightAfter as CodePane).tabs.map((t) => t.path)).toEqual([A]);
  });

  it("exchanges two panes in the same column", () => {
    const l1 = openFile(EMPTY_LAYOUT, A);
    const l2 = splitDown(l1);
    const l3 = openFile(l2, B);
    const [topBefore, bottomBefore] = getAllPanes(l3);

    const swapped = swapPanes(l3, topBefore!.id, bottomBefore!.id);
    const [topAfter, bottomAfter] = getAllPanes(swapped);
    expect(topAfter!.id).toBe(bottomBefore!.id);
    expect(bottomAfter!.id).toBe(topBefore!.id);
  });

  it("keeps slot weights pinned to positions", () => {
    const l1 = openFile(EMPTY_LAYOUT, A);
    const l2 = splitRight(l1);
    const l3 = openFile(l2, B);
    const [left, right] = getAllPanes(l3);
    const leftW = 2;
    const rightW = 5;
    const withWeights: Layout = {
      ...l3,
      columns: l3.columns.map((col, ci) => ({
        ...col,
        panes: col.panes.map((p) => ({ ...p, weight: ci === 0 ? leftW : rightW })),
      })),
    };

    const swapped = swapPanes(withWeights, left!.id, right!.id);
    const [leftAfter, rightAfter] = getAllPanes(swapped);
    // Slot sizes stay put (leftW at position 0, rightW at position 1); only identities swap.
    expect(paneWeight(leftAfter!)).toBe(leftW);
    expect(paneWeight(rightAfter!)).toBe(rightW);
    expect(leftAfter!.id).toBe(right!.id);
    expect(rightAfter!.id).toBe(left!.id);
  });

  it("is a no-op when swapping a pane with itself", () => {
    const l1 = openFile(EMPTY_LAYOUT, A);
    const l2 = splitRight(l1);
    const [, right] = getAllPanes(l2);
    expect(swapPanes(l2, right!.id, right!.id)).toBe(l2);
  });

  it("is a no-op when either pane id is unknown", () => {
    const l1 = openFile(EMPTY_LAYOUT, A);
    const [only] = getAllPanes(l1);
    expect(swapPanes(l1, only!.id, "nope")).toBe(l1);
    expect(swapPanes(l1, "nope", only!.id)).toBe(l1);
  });

  it("swaps a code pane with a browser pane", () => {
    const l1 = openFile(EMPTY_LAYOUT, A);
    const l2 = splitRight(l1, "assetBrowser");
    const [codeBefore, browserBefore] = getAllPanes(l2);
    expect(codeBefore!.kind).toBe("code");
    expect(browserBefore!.kind).toBe("assetBrowser");

    const swapped = swapPanes(l2, codeBefore!.id, browserBefore!.id);
    const [leftAfter, rightAfter] = getAllPanes(swapped);
    expect(leftAfter!.kind).toBe("assetBrowser");
    expect(rightAfter!.kind).toBe("code");
  });
});

describe("setBrowserPaneState", () => {
  it("stores the state blob on a browser pane", () => {
    const l = splitRight(EMPTY_LAYOUT, "assetBrowser");
    const paneId = l.activePaneId!;
    const next = setBrowserPaneState(l, paneId, DEFAULT_ASSET_BROWSER_STATE);
    const pane = findPane(next, paneId) as BrowserPane;
    expect(pane.state).toBe(DEFAULT_ASSET_BROWSER_STATE);
  });

  it("is a no-op when the state reference hasn't changed", () => {
    const l = splitRight(EMPTY_LAYOUT, "templateBrowser");
    const paneId = l.activePaneId!;
    const set = setBrowserPaneState(l, paneId, DEFAULT_TEMPLATE_BROWSER_STATE);
    expect(setBrowserPaneState(set, paneId, DEFAULT_TEMPLATE_BROWSER_STATE)).toBe(set);
  });

  it("is a no-op for an unknown pane id", () => {
    const l = splitRight(EMPTY_LAYOUT, "assetBrowser");
    expect(setBrowserPaneState(l, "missing", DEFAULT_ASSET_BROWSER_STATE)).toBe(l);
  });

  it("is a no-op on a code pane (states apply only to browsers)", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    expect(setBrowserPaneState(l, l.activePaneId!, DEFAULT_ASSET_BROWSER_STATE)).toBe(l);
  });

  it("preserves the pane's weight across a state update", () => {
    const l = splitRight(EMPTY_LAYOUT, "assetBrowser");
    const paneId = l.activePaneId!;
    const weighted = setPaneWeight(l, paneId, 3);
    const next = setBrowserPaneState(weighted, paneId, DEFAULT_ASSET_BROWSER_STATE);
    expect(paneWeight(findPane(next, paneId)!)).toBe(3);
  });
});

describe("splitAtEdgeWithPath", () => {
  it("creates a new right column with the given path", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const next = splitAtEdgeWithPath(l, toId, B, "right");
    expect(next.columns).toHaveLength(2);
    expect(asCode(next.columns[1]!.panes[0]).tabs.map((t) => t.path)).toEqual([B]);
    // Source is untouched (unlike splitWithTab, no removal from origin).
    expect(asCode(findPane(next, toId)).tabs.map((t) => t.path)).toEqual([A]);
  });

  it("creates a new pane below the target with the given path", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const next = splitAtEdgeWithPath(l, toId, B, "bottom");
    expect(next.columns[0]!.panes).toHaveLength(2);
    expect(asCode(next.columns[0]!.panes[1]).tabs.map((t) => t.path)).toEqual([B]);
  });

  it("returns the layout unchanged for an unknown target pane", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    expect(splitAtEdgeWithPath(l, "missing", B, "right")).toBe(l);
  });

  it("makes the newly-created pane active", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const next = splitAtEdgeWithPath(l, l.activePaneId!, B, "right");
    const active = findPane(next, next.activePaneId!);
    expect(active!.kind).toBe("code");
    expect(asCode(active).tabs.map((t) => t.path)).toEqual([B]);
  });
});

describe("insertPaneAtEdge", () => {
  it("inserts a fully-constructed code pane to the right as a new column", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const newPane: CodePane = {
      id: "new-1",
      kind: "code",
      tabs: [{ path: B, name: "B" }],
      activeTab: B,
    };
    const next = insertPaneAtEdge(l, toId, newPane, "right");
    expect(next.columns).toHaveLength(2);
    expect(next.columns[1]!.panes[0]!.id).toBe("new-1");
    expect(next.activePaneId).toBe("new-1");
  });

  it("inserts a browser pane below the target", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const newPane: BrowserPane = {
      id: "bp-1",
      kind: "templateBrowser",
      state: DEFAULT_TEMPLATE_BROWSER_STATE,
    };
    const next = insertPaneAtEdge(l, toId, newPane, "bottom");
    expect(next.columns[0]!.panes.map((p) => p.id)).toEqual([toId, "bp-1"]);
    expect((findPane(next, "bp-1") as BrowserPane).state).toBe(DEFAULT_TEMPLATE_BROWSER_STATE);
  });

  it("is a no-op when the target pane doesn't exist", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const newPane: CodePane = { id: "x", kind: "code", tabs: [], activeTab: null };
    expect(insertPaneAtEdge(l, "missing", newPane, "right")).toBe(l);
  });

  it("gives the new pane the average weight of existing panes in the column", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const withHeavy = setPaneWeight(l, toId, 5);
    const newPane: CodePane = { id: "np", kind: "code", tabs: [], activeTab: null };
    const next = insertPaneAtEdge(withHeavy, toId, newPane, "bottom");
    expect(paneWeight(findPane(next, "np")!)).toBeCloseTo(5);
  });
});

describe("insertCrossWindowPane", () => {
  it("rebuilds a code pane from a tabs list + active tab", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const next = insertCrossWindowPane(
      l,
      toId,
      "code",
      [{ path: B }, { path: C }],
      C,
      undefined,
      "right",
    );
    expect(next.columns).toHaveLength(2);
    const added = asCode(next.columns[1]!.panes[0]);
    expect(added.tabs.map((t) => t.path)).toEqual([B, C]);
    expect(added.activeTab).toBe(C);
  });

  it("rebuilds a browser pane with its persisted state", () => {
    const l = openFile(EMPTY_LAYOUT, A);
    const toId = l.activePaneId!;
    const next = insertCrossWindowPane(
      l,
      toId,
      "assetBrowser",
      [],
      null,
      DEFAULT_ASSET_BROWSER_STATE,
      "bottom",
    );
    const added = next.columns[0]!.panes[1]! as BrowserPane;
    expect(added.kind).toBe("assetBrowser");
    expect(added.state).toBe(DEFAULT_ASSET_BROWSER_STATE);
  });

  it("is a no-op for an unknown target pane", () => {
    expect(
      insertCrossWindowPane(EMPTY_LAYOUT, "missing", "code", [{ path: A }], A, undefined, "right"),
    ).toBe(EMPTY_LAYOUT);
  });
});

describe("reorderTab", () => {
  function withThreeTabs(): { layout: Layout; paneId: string } {
    const l = openFile(openFile(openFile(EMPTY_LAYOUT, A), B), C);
    return { layout: l, paneId: l.activePaneId! };
  }

  it("moves a tab earlier in the array", () => {
    const { layout, paneId } = withThreeTabs();
    const next = reorderTab(layout, paneId, C, 0);
    expect(asCode(findPane(next, paneId)).tabs.map((t) => t.path)).toEqual([C, A, B]);
  });

  it("moves a tab later in the array (accounting for removal shift)", () => {
    const { layout, paneId } = withThreeTabs();
    // Request: put A at the tail (index 3). After removing A, list is [B, C]
    // and inserting at index 3 clamps to 2 → [B, C, A].
    const next = reorderTab(layout, paneId, A, 3);
    expect(asCode(findPane(next, paneId)).tabs.map((t) => t.path)).toEqual([B, C, A]);
  });

  it("is a no-op when the target index equals the current index", () => {
    const { layout, paneId } = withThreeTabs();
    expect(reorderTab(layout, paneId, B, 1)).toBe(layout);
  });

  it("is a no-op for an unknown path", () => {
    const { layout, paneId } = withThreeTabs();
    expect(reorderTab(layout, paneId, "/nope", 0)).toBe(layout);
  });

  it("is a no-op for an unknown pane", () => {
    const { layout } = withThreeTabs();
    expect(reorderTab(layout, "missing", A, 0)).toBe(layout);
  });

  it("is a no-op on a browser pane", () => {
    const l = splitRight(EMPTY_LAYOUT, "assetBrowser");
    expect(reorderTab(l, l.activePaneId!, A, 0)).toBe(l);
  });

  it("clamps a target index larger than tabs.length to the end", () => {
    const { layout, paneId } = withThreeTabs();
    const next = reorderTab(layout, paneId, B, 99);
    expect(asCode(findPane(next, paneId)).tabs.map((t) => t.path)).toEqual([A, C, B]);
  });

  it("keeps the active tab pointing at the same path after reorder", () => {
    const { layout, paneId } = withThreeTabs();
    const active = asCode(findPane(layout, paneId)).activeTab;
    const next = reorderTab(layout, paneId, A, 2);
    expect(asCode(findPane(next, paneId)).activeTab).toBe(active);
  });
});

describe("openFile with non-default column weights", () => {
  it("gives a brand-new column the average of existing column weights", () => {
    // Regression: open-into-browser-only layouts used to hand the new code
    // column a default weight of 1 while survivors had inherited redistributed
    // weight, so each close/reopen cycle shrank the new code pane.
    const onlyBrowser = splitRight(EMPTY_LAYOUT, "assetBrowser");
    const wide = setColumnWeight(onlyBrowser, onlyBrowser.columns[0]!.id, 5);
    const opened = openFile(wide, C);
    expect(columnWeight(opened.columns[0]!)).toBe(5);
    expect(columnWeight(opened.columns[1]!)).toBeCloseTo(5);
  });
});
