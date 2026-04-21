import { basename } from "./path.ts";

export interface Tab {
  readonly path: string;
  readonly name: string;
}

export type PaneKind = "code" | "assetBrowser" | "templateBrowser";

export interface CodePane {
  readonly id: string;
  readonly kind: "code";
  readonly tabs: readonly Tab[];
  readonly activeTab: string | null;
  /** Flex weight relative to siblings in the same column. Treated as 1 when absent. */
  readonly weight?: number;
}

export interface BrowserPane {
  readonly id: string;
  readonly kind: "assetBrowser" | "templateBrowser";
  /** Flex weight relative to siblings in the same column. Treated as 1 when absent. */
  readonly weight?: number;
}

export type Pane = CodePane | BrowserPane;

export interface Column {
  readonly id: string;
  readonly panes: readonly Pane[];
  /** Flex weight relative to sibling columns. Treated as 1 when absent. */
  readonly weight?: number;
}

export type SplitEdge = "left" | "right" | "top" | "bottom";

export type MovePaneTarget =
  | { readonly kind: "asNewColumn"; readonly index: number }
  | { readonly kind: "intoColumn"; readonly columnId: string; readonly index: number };

export interface Layout {
  readonly columns: readonly Column[];
  readonly activePaneId: string | null;
}

export const EMPTY_LAYOUT: Layout = { columns: [], activePaneId: null };

/** Human-facing metadata for non-code pane kinds (used by the palette). */
export const BROWSER_KIND_META: Record<BrowserPane["kind"], { label: string }> = {
  assetBrowser: { label: "Asset Browser" },
  templateBrowser: { label: "Template Browser" },
};

let idCounter = 0;
function nextId(prefix: string): string {
  idCounter += 1;
  return `${prefix}_${Date.now().toString(36)}_${idCounter.toString(36)}`;
}

function emptyPane(kind: PaneKind): Pane {
  if (kind === "code") return { id: nextId("p"), kind, tabs: [], activeTab: null };
  return { id: nextId("p"), kind };
}

function emptyColumn(pane: Pane): Column {
  return { id: nextId("c"), panes: [pane] };
}

/** Pane's flex weight (defaults to 1 when not set). */
export function paneWeight(pane: Pane): number {
  return typeof pane.weight === "number" && pane.weight > 0 ? pane.weight : 1;
}

/** Column's flex weight (defaults to 1 when not set). */
export function columnWeight(column: Column): number {
  return typeof column.weight === "number" && column.weight > 0 ? column.weight : 1;
}

function avgColumnWeight(columns: readonly Column[]): number {
  if (columns.length === 0) return 1;
  let sum = 0;
  for (const c of columns) sum += columnWeight(c);
  return sum / columns.length;
}

function avgPaneWeight(panes: readonly Pane[]): number {
  if (panes.length === 0) return 1;
  let sum = 0;
  for (const p of panes) sum += paneWeight(p);
  return sum / panes.length;
}

function withWeight<T extends Pane | Column>(item: T, weight: number): T {
  return { ...item, weight };
}

function withoutWeight<T extends Pane>(pane: T): T {
  if (pane.weight === undefined) return pane;
  const { weight: _w, ...rest } = pane;
  return rest as T;
}

export function findPane(layout: Layout, paneId: string): Pane | null {
  for (const col of layout.columns) {
    for (const pane of col.panes) {
      if (pane.id === paneId) return pane;
    }
  }
  return null;
}

export function getActivePane(layout: Layout): Pane | null {
  if (layout.activePaneId === null) return null;
  return findPane(layout, layout.activePaneId);
}

export function getActiveCodePane(layout: Layout): CodePane | null {
  const pane = getActivePane(layout);
  return pane !== null && pane.kind === "code" ? pane : null;
}

export function getAllPanes(layout: Layout): Pane[] {
  const out: Pane[] = [];
  for (const col of layout.columns) for (const pane of col.panes) out.push(pane);
  return out;
}

/** Walk every code pane and collect every open file path. */
export function getAllOpenPaths(layout: Layout): Set<string> {
  const set = new Set<string>();
  for (const col of layout.columns) {
    for (const pane of col.panes) {
      if (pane.kind !== "code") continue;
      for (const tab of pane.tabs) set.add(tab.path);
    }
  }
  return set;
}

function findPaneCoords(layout: Layout, paneId: string): { col: number; pane: number } | null {
  for (let c = 0; c < layout.columns.length; c++) {
    const col = layout.columns[c]!;
    for (let p = 0; p < col.panes.length; p++) {
      if (col.panes[p]!.id === paneId) return { col: c, pane: p };
    }
  }
  return null;
}

function replacePane(layout: Layout, paneId: string, next: Pane): Layout {
  let changed = false;
  const columns = layout.columns.map((col) => ({
    ...col,
    panes: col.panes.map((pane) => {
      if (pane.id !== paneId) return pane;
      changed = true;
      return next;
    }),
  }));
  return changed ? { ...layout, columns } : layout;
}

function withTab(pane: CodePane, path: string): CodePane {
  if (pane.tabs.some((t) => t.path === path)) {
    return pane.activeTab === path ? pane : { ...pane, activeTab: path };
  }
  return {
    ...pane,
    tabs: [...pane.tabs, { path, name: basename(path) }],
    activeTab: path,
  };
}

function withoutTabs(pane: CodePane, paths: ReadonlySet<string>): CodePane {
  const tabs = pane.tabs.filter((t) => !paths.has(t.path));
  if (tabs.length === pane.tabs.length) return pane;
  let activeTab = pane.activeTab;
  if (activeTab !== null && paths.has(activeTab)) {
    activeTab = tabs.length > 0 ? tabs[tabs.length - 1]!.path : null;
  }
  return { ...pane, tabs, activeTab };
}

function findCodePane(layout: Layout): CodePane | null {
  for (const col of layout.columns) {
    for (const pane of col.panes) {
      if (pane.kind === "code") return pane;
    }
  }
  return null;
}

/**
 * Open a file in the active code pane (or the first code pane found, or a new
 * one if none exist). Browser panes are skipped — files always land in code.
 */
export function openFile(layout: Layout, path: string, paneId: string | null = null): Layout {
  const explicit = paneId !== null ? findPane(layout, paneId) : null;
  let target: CodePane | null = null;
  if (explicit !== null && explicit.kind === "code") {
    target = explicit;
  } else {
    const active = getActivePane(layout);
    if (active !== null && active.kind === "code") target = active;
    else target = findCodePane(layout);
  }

  if (target === null) {
    const pane = withTab(emptyPane("code") as CodePane, path);
    const col = emptyColumn(pane);
    return { columns: [...layout.columns, col], activePaneId: pane.id };
  }

  const next = withTab(target, path);
  return { ...replacePane(layout, target.id, next), activePaneId: target.id };
}

/** Close the listed tabs from a specific code pane. Empty panes (and resulting empty columns) are pruned. */
export function closeTabs(layout: Layout, paneId: string, paths: readonly string[]): Layout {
  if (paths.length === 0) return layout;
  const target = findPane(layout, paneId);
  if (target === null || target.kind !== "code") return layout;
  const set = new Set(paths);
  const nextPane = withoutTabs(target, set);
  if (nextPane === target) return layout;
  if (nextPane.tabs.length > 0) {
    return replacePane(layout, paneId, nextPane);
  }
  return removePane(layout, paneId);
}

/** Close every tab matching any of the listed paths across every code pane. */
export function closeTabsEverywhere(layout: Layout, paths: readonly string[]): Layout {
  if (paths.length === 0) return layout;
  let next = layout;
  for (const pane of getAllPanes(layout)) {
    if (pane.kind !== "code") continue;
    next = closeTabs(next, pane.id, paths);
  }
  return next;
}

function removePane(layout: Layout, paneId: string): Layout {
  const coords = findPaneCoords(layout, paneId);
  if (coords === null) return layout;
  const columns = layout.columns
    .map((col, ci) => {
      if (ci !== coords.col) return col;
      return { ...col, panes: col.panes.filter((p) => p.id !== paneId) };
    })
    .filter((col) => col.panes.length > 0);

  let activePaneId = layout.activePaneId;
  if (activePaneId === paneId) activePaneId = pickNextActive(layout, coords);

  return { columns, activePaneId };
}

function pickNextActive(layout: Layout, removed: { col: number; pane: number }): string | null {
  const col = layout.columns[removed.col];
  if (col !== undefined) {
    const sibling = col.panes[removed.pane + 1] ?? col.panes[removed.pane - 1];
    if (sibling !== undefined) return sibling.id;
  }
  for (let dist = 1; dist < layout.columns.length; dist++) {
    for (const ci of [removed.col + dist, removed.col - dist]) {
      const c = layout.columns[ci];
      if (c !== undefined && c.panes.length > 0) return c.panes[0]!.id;
    }
  }
  return null;
}

/** Make a pane active and (optionally) select one of its tabs. No-op for browser panes. */
export function selectTab(layout: Layout, paneId: string, path: string): Layout {
  const target = findPane(layout, paneId);
  if (target === null || target.kind !== "code") return layout;
  if (target.activeTab === path) {
    return layout.activePaneId === paneId ? layout : { ...layout, activePaneId: paneId };
  }
  if (!target.tabs.some((t) => t.path === path)) return layout;
  return {
    ...replacePane(layout, paneId, { ...target, activeTab: path }),
    activePaneId: paneId,
  };
}

export function setActivePane(layout: Layout, paneId: string): Layout {
  if (layout.activePaneId === paneId) return layout;
  if (findPane(layout, paneId) === null) return layout;
  return { ...layout, activePaneId: paneId };
}

/** Add a new pane to the right (new column). */
export function splitRight(layout: Layout, kind: PaneKind = "code"): Layout {
  const pane = emptyPane(kind);
  const newCol = withWeight(emptyColumn(pane), avgColumnWeight(layout.columns));
  const activeCoords =
    layout.activePaneId === null ? null : findPaneCoords(layout, layout.activePaneId);
  const idx = activeCoords === null ? layout.columns.length : activeCoords.col + 1;
  const columns = [...layout.columns.slice(0, idx), newCol, ...layout.columns.slice(idx)];
  return { columns, activePaneId: pane.id };
}

/** Add a new pane below the active pane (in the same column). */
export function splitDown(layout: Layout, kind: PaneKind = "code"): Layout {
  if (layout.activePaneId === null || layout.columns.length === 0) {
    return splitRight(layout, kind);
  }
  const coords = findPaneCoords(layout, layout.activePaneId);
  if (coords === null) return splitRight(layout, kind);
  const targetCol = layout.columns[coords.col]!;
  const newPane = withWeight(emptyPane(kind), avgPaneWeight(targetCol.panes));
  const columns = layout.columns.map((col, ci) => {
    if (ci !== coords.col) return col;
    return {
      ...col,
      panes: [...col.panes.slice(0, coords.pane + 1), newPane, ...col.panes.slice(coords.pane + 1)],
    };
  });
  return { columns, activePaneId: newPane.id };
}

/** Close a pane (prunes column when the column becomes empty). */
export function closePane(layout: Layout, paneId: string): Layout {
  return removePane(layout, paneId);
}

/**
 * Move a pane to a new position. Source pane is removed from its current
 * column (which may be pruned if it becomes empty) and inserted at the target.
 */
export function movePane(layout: Layout, paneId: string, target: MovePaneTarget): Layout {
  const coords = findPaneCoords(layout, paneId);
  if (coords === null) return layout;
  const sourceColumn = layout.columns[coords.col]!;
  const sourcePane = sourceColumn.panes[coords.pane]!;

  // Moving the only pane in a column to a position within the same column is a no-op.
  if (
    target.kind === "intoColumn" &&
    target.columnId === sourceColumn.id &&
    sourceColumn.panes.length === 1
  ) {
    return layout;
  }

  // Build the layout without the source pane (and prune empty columns).
  const sourceColumnPruned = sourceColumn.panes.length === 1;
  const removedColumns = layout.columns
    .map((col, ci) => {
      if (ci !== coords.col) return col;
      return { ...col, panes: col.panes.filter((p) => p.id !== paneId) };
    })
    .filter((col) => col.panes.length > 0);

  // The moved pane's prior weight relates to its old siblings; reset so it
  // takes the average share of its new context.
  const movedPaneBare = withoutWeight(sourcePane);

  if (target.kind === "asNewColumn") {
    let insertAt = target.index;
    if (sourceColumnPruned && coords.col < insertAt) insertAt -= 1;
    insertAt = Math.max(0, Math.min(insertAt, removedColumns.length));
    const newColumn = withWeight<Column>(
      { id: nextId("c"), panes: [movedPaneBare] },
      avgColumnWeight(removedColumns),
    );
    const columns = [
      ...removedColumns.slice(0, insertAt),
      newColumn,
      ...removedColumns.slice(insertAt),
    ];
    return { columns, activePaneId: paneId };
  }

  // intoColumn — target column must still exist after removal.
  if (!removedColumns.some((c) => c.id === target.columnId)) return layout;
  const sameColumn = target.columnId === sourceColumn.id;
  const columns = removedColumns.map((col) => {
    if (col.id !== target.columnId) return col;
    let insertAt = target.index;
    if (sameColumn && coords.pane < insertAt) insertAt -= 1;
    insertAt = Math.max(0, Math.min(insertAt, col.panes.length));
    const insertPane = withWeight(movedPaneBare, avgPaneWeight(col.panes));
    return {
      ...col,
      panes: [...col.panes.slice(0, insertAt), insertPane, ...col.panes.slice(insertAt)],
    };
  });
  return { columns, activePaneId: paneId };
}

/**
 * Split a target pane in the given direction with a brand-new code pane that
 * receives the dragged tab. The tab is removed from its source pane (which may
 * be pruned). Returns the layout unchanged if the source/target/path is invalid.
 */
export function splitWithTab(
  layout: Layout,
  fromPaneId: string,
  toPaneId: string,
  path: string,
  edge: SplitEdge,
): Layout {
  const from = findPane(layout, fromPaneId);
  const to = findPane(layout, toPaneId);
  if (from === null || to === null || from.kind !== "code") return layout;
  if (!from.tabs.some((t) => t.path === path)) return layout;

  const afterRemove = closeTabs(layout, fromPaneId, [path]);
  const toCoords = findPaneCoords(afterRemove, toPaneId);
  if (toCoords === null) return layout;

  const newPane: CodePane = {
    id: nextId("p"),
    kind: "code",
    tabs: [{ path, name: basename(path) }],
    activeTab: path,
  };

  if (edge === "left" || edge === "right") {
    const newCol = withWeight<Column>(
      { id: nextId("c"), panes: [newPane] },
      avgColumnWeight(afterRemove.columns),
    );
    const insertAt = edge === "right" ? toCoords.col + 1 : toCoords.col;
    const columns = [
      ...afterRemove.columns.slice(0, insertAt),
      newCol,
      ...afterRemove.columns.slice(insertAt),
    ];
    return { columns, activePaneId: newPane.id };
  }

  const insertAt = edge === "bottom" ? toCoords.pane + 1 : toCoords.pane;
  const columns = afterRemove.columns.map((col, ci) => {
    if (ci !== toCoords.col) return col;
    const insertPane = withWeight(newPane, avgPaneWeight(col.panes));
    return {
      ...col,
      panes: [...col.panes.slice(0, insertAt), insertPane, ...col.panes.slice(insertAt)],
    };
  });
  return { columns, activePaneId: newPane.id };
}

/**
 * Move a pane to a specific edge of another pane. The edge determines the
 * insertion: left/right become new sibling columns, top/bottom become new
 * sibling panes within the target's column.
 */
export function movePaneToEdge(
  layout: Layout,
  paneId: string,
  targetPaneId: string,
  edge: SplitEdge,
): Layout {
  if (paneId === targetPaneId) return layout;
  const targetCoords = findPaneCoords(layout, targetPaneId);
  if (targetCoords === null) return layout;
  const targetColumn = layout.columns[targetCoords.col]!;

  if (edge === "left" || edge === "right") {
    const insertAt = edge === "right" ? targetCoords.col + 1 : targetCoords.col;
    return movePane(layout, paneId, { kind: "asNewColumn", index: insertAt });
  }
  const insertAt = edge === "bottom" ? targetCoords.pane + 1 : targetCoords.pane;
  return movePane(layout, paneId, {
    kind: "intoColumn",
    columnId: targetColumn.id,
    index: insertAt,
  });
}

/**
 * Swap a pane's kind in place, preserving id, position, and weight. Useful for
 * letting an empty code pane become a browser pane without restructuring.
 */
export function convertPane(layout: Layout, paneId: string, kind: PaneKind): Layout {
  const target = findPane(layout, paneId);
  if (target === null || target.kind === kind) return layout;
  let replacement: Pane =
    kind === "code" ? { id: target.id, kind, tabs: [], activeTab: null } : { id: target.id, kind };
  if (target.weight !== undefined) replacement = withWeight(replacement, target.weight);
  return replacePane(layout, paneId, replacement);
}

/** Set a pane's flex weight. */
export function setPaneWeight(layout: Layout, paneId: string, weight: number): Layout {
  if (!(weight > 0)) return layout;
  const target = findPane(layout, paneId);
  if (target === null || paneWeight(target) === weight) return layout;
  return replacePane(layout, paneId, { ...target, weight } as Pane);
}

/** Set a column's flex weight. */
export function setColumnWeight(layout: Layout, columnId: string, weight: number): Layout {
  if (!(weight > 0)) return layout;
  let changed = false;
  const columns = layout.columns.map((col) => {
    if (col.id !== columnId || columnWeight(col) === weight) return col;
    changed = true;
    return { ...col, weight };
  });
  return changed ? { ...layout, columns } : layout;
}

/** Move a tab from one code pane to another. Both must be code panes. */
export function moveTab(
  layout: Layout,
  fromPaneId: string,
  toPaneId: string,
  path: string,
): Layout {
  if (fromPaneId === toPaneId) return layout;
  const from = findPane(layout, fromPaneId);
  const to = findPane(layout, toPaneId);
  if (from === null || to === null) return layout;
  if (from.kind !== "code" || to.kind !== "code") return layout;
  if (!from.tabs.some((t) => t.path === path)) return layout;

  const afterRemove = closeTabs(layout, fromPaneId, [path]);
  // closeTabs may have pruned the source pane (and its column). The destination
  // pane id is still valid because tabs were never moved out of it.
  if (findPane(afterRemove, toPaneId) === null) return layout;
  return openFile(afterRemove, path, toPaneId);
}

/** Rewrite all tab paths in code panes via the supplied mapper. */
export function remapPaths(layout: Layout, remap: (path: string) => string): Layout {
  let changed = false;
  const columns = layout.columns.map((col) => ({
    ...col,
    panes: col.panes.map((pane) => {
      if (pane.kind !== "code") return pane;
      let paneChanged = false;
      const tabs = pane.tabs.map((t) => {
        const next = remap(t.path);
        if (next === t.path) return t;
        paneChanged = true;
        return { path: next, name: basename(next) };
      });
      const activeTab = pane.activeTab === null ? null : remap(pane.activeTab);
      if (!paneChanged && activeTab === pane.activeTab) return pane;
      changed = true;
      return { ...pane, tabs, activeTab };
    }),
  }));
  return changed ? { ...layout, columns } : layout;
}

const STORAGE_PREFIX = "jiangyu:layout:";

export function saveLayout(projectPath: string, layout: Layout): void {
  try {
    localStorage.setItem(STORAGE_PREFIX + projectPath, JSON.stringify(layout));
  } catch {
    // Quota exceeded or storage unavailable — drop the save silently.
  }
}

export function loadLayout(projectPath: string): Layout | null {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + projectPath);
    if (raw === null) return null;
    const parsed = JSON.parse(raw) as Layout;
    if (!isValidLayout(parsed)) return null;
    return parsed;
  } catch {
    return null;
  }
}

function isValidPane(p: unknown): p is Pane {
  if (typeof p !== "object" || p === null) return false;
  const obj = p as { id?: unknown; kind?: unknown };
  if (typeof obj.id !== "string") return false;
  if (obj.kind === "code") {
    const cp = p as CodePane;
    if (!Array.isArray(cp.tabs)) return false;
    if (cp.activeTab !== null && typeof cp.activeTab !== "string") return false;
    return cp.tabs.every((t) => typeof t.path === "string" && typeof t.name === "string");
  }
  return obj.kind === "assetBrowser" || obj.kind === "templateBrowser";
}

function isValidLayout(value: unknown): value is Layout {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Layout;
  if (!Array.isArray(v.columns)) return false;
  if (v.activePaneId !== null && typeof v.activePaneId !== "string") return false;
  return v.columns.every(
    (c: Column) => typeof c.id === "string" && Array.isArray(c.panes) && c.panes.every(isValidPane),
  );
}
