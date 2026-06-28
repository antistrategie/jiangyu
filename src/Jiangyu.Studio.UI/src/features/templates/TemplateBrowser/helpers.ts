// Pure helpers for the template browser. Side-effect-free so the JSX modules
// alongside can rely on stable identity at import time and the unit tests
// can call them without booting React.

import type { InspectedFieldNode, TemplateInstanceEntry } from "@shared/rpc";

/**
 * Compact single-line summary of an inspected field value. Drives the
 * collapsed-row preview in TemplateBrowser and TemplateDetail. Recurses
 * into short, all-scalar arrays so something like `[1, 2, 3]` renders
 * inline instead of `[3 items]`.
 */
export function formatValue(value: InspectedFieldNode | null): string {
  if (!value) return "…";
  if (value.null) return "null";
  // eslint-disable-next-line @typescript-eslint/no-base-to-string -- String() handles unknown at runtime, and the guard above excludes null/undefined.
  if (value.value !== undefined && value.value !== null) return String(value.value);
  if (value.kind === "reference" && value.reference) {
    return value.reference.name ?? `[pathId=${value.reference.pathId}]`;
  }
  if (value.kind === "assetReference") {
    // The inspector emits "reference" for vanilla asset-typed fields today,
    // so this branch covers the modder-edited pathway where a patch has
    // been merged into the inspected view as an AssetReference value.
    const name = (value as { asset?: { name?: string } | null }).asset?.name;
    return name ? `→ ${name}` : "→ ?";
  }
  if (value.kind === "string" && value.value !== null) return `"${String(value.value)}"`;
  if (value.kind === "array") {
    const count = value.count ?? value.elements?.length ?? 0;
    if (count === 0) return "[]";
    const allSimple =
      value.elements?.every((e) => e.value !== undefined || e.kind === "string" || e.null) ?? false;
    if (allSimple && value.elements && value.elements.length <= 4 && value.elements.length > 0) {
      return "[" + value.elements.map((e) => formatValue(e)).join(", ") + "]";
    }
    return `[${count} items]`;
  }
  if (value.kind === "object") {
    const fieldCount = value.fields?.length ?? 0;
    return `{ ${fieldCount} field${fieldCount !== 1 ? "s" : ""} }`;
  }
  return "";
}

/**
 * Single-character preview of one cell in a matrix-typed field. Bools render
 * as ■ / ·; numeric / enum scalars render as their string form; missing or
 * unknown cells render as `·`.
 */
export function formatMatrixCell(cell: InspectedFieldNode | undefined): string {
  if (!cell) return "·";
  if (cell.kind === "bool") return cell.value === true ? "■" : "·";
  if (cell.value !== undefined && cell.value !== null) {
    // eslint-disable-next-line @typescript-eslint/no-base-to-string -- numeric or enum scalar
    return String(cell.value);
  }
  return "·";
}

/**
 * Whether the node renders as a single inline value (no expand affordance).
 * Used by row-renderers to decide between a leaf cell and a tree-expand row.
 */
export function valueNodeKindIsScalar(node: InspectedFieldNode): boolean {
  return node.kind !== "array" && node.kind !== "object" && node.kind !== "reference";
}

/**
 * Maps enum members (numeric value → name) to an index-keyed dictionary for
 * fast lookup in named-array rendering. Returns null when the input is
 * null/undefined so callers can short-circuit without an extra check.
 */
export function buildNamedArrayLabelMap(
  members: readonly { readonly name: string; readonly value: number }[] | null | undefined,
): Record<number, string> | null {
  if (!members) return null;
  const map: Record<number, string> = {};
  for (const m of members) {
    map[m.value] = m.name;
  }
  return map;
}

/**
 * Looks up the enum member name for a numeric leaf value. Returns null when
 * the value isn't a finite integer or isn't a defined member of the enum;
 * callers fall back to displaying the raw value so unusual values stay
 * visible rather than disappearing into "?".
 */
export function resolveEnumLeafLabel(
  rawValue: unknown,
  labelMap: Record<number, string> | null,
): string | null {
  if (!labelMap) return null;
  if (typeof rawValue !== "number" || !Number.isFinite(rawValue)) return null;
  return labelMap[rawValue] ?? null;
}

/**
 * Stable identity key for a template instance. Pairs `(collection, pathId)`
 * because neither component is unique on its own — a name can repeat across
 * collections, and a pathId can repeat across collections. Used for React
 * keys, nav-history entries, and instance lookup maps.
 */
export function instanceKey(inst: TemplateInstanceEntry): string {
  return `${inst.identity.collection}:${inst.identity.pathId}`;
}

/**
 * Secondary index for resolving an inspected reference (pathId + optional
 * name) to an instance key in O(1). A reference's fileId is a dependency
 * index, not a collection name, so the pathId alone can be ambiguous
 * across collections; the name-qualified map handles references that
 * carry a name. Both maps keep the first instance encountered, matching
 * iteration order over the instance list.
 */
export interface ReferenceTargetIndex {
  readonly byPathId: ReadonlyMap<number, string>;
  readonly byPathIdAndName: ReadonlyMap<string, string>;
  // pathIds owned by more than one instance (pathIds are per-collection). A
  // name-less reference at such a pathId cannot be resolved unambiguously.
  readonly ambiguousPathIds: ReadonlySet<number>;
}

// U+0000 can't appear in a template name, so the composite key encodes the
// (pathId, name) pair without collision risk.
function referenceNameKey(pathId: number, name: string): string {
  return `${pathId}\u0000${name}`;
}

export function buildReferenceTargetIndex(
  instances: readonly TemplateInstanceEntry[],
): ReferenceTargetIndex {
  const byPathId = new Map<number, string>();
  const ambiguousPathIds = new Set<number>();
  const byPathIdAndName = new Map<string, string>();
  for (const inst of instances) {
    const key = instanceKey(inst);
    if (byPathId.has(inst.identity.pathId)) ambiguousPathIds.add(inst.identity.pathId);
    else byPathId.set(inst.identity.pathId, key);
    const nameKey = referenceNameKey(inst.identity.pathId, inst.name);
    if (!byPathIdAndName.has(nameKey)) byPathIdAndName.set(nameKey, key);
  }
  return { byPathId, byPathIdAndName, ambiguousPathIds };
}

/** Instance key for a reference target, or null when nothing matches. A
 *  named reference must match on both pathId and name; a missing name
 *  (undefined or empty) takes the instance with that pathId only when it is
 *  unambiguous; a null name never matches (instance names are always
 *  non-empty strings). Mirrors the backend resolver in TemplateIndexService. */
export function resolveReferenceTargetKey(
  index: ReferenceTargetIndex,
  pathId: number,
  name: string | null | undefined,
): string | null {
  if (name === undefined || name === "") {
    return index.ambiguousPathIds.has(pathId) ? null : (index.byPathId.get(pathId) ?? null);
  }
  if (name === null) return null;
  return index.byPathIdAndName.get(referenceNameKey(pathId, name)) ?? null;
}

/**
 * Push a new key onto the nav history relative to the current index,
 * truncating any forward branches. Mirrors browser-style nav semantics:
 * navigating somewhere new while in the middle of the history discards
 * the trail past the current position.
 *
 * Returns the new (history, index) pair. Pure — caller updates state.
 */
export function pushNavEntry(
  history: readonly string[],
  index: number,
  key: string,
): { history: string[]; index: number } {
  const truncated = history.slice(0, index + 1);
  return { history: [...truncated, key], index: truncated.length };
}

/**
 * Move backward in nav history. Returns the new (index, target key) when
 * a back step is possible; null when at the start. Caller pushes the
 * target key into the focus state.
 */
export function navStepBack(
  history: readonly string[],
  index: number,
): { index: number; key: string } | null {
  if (index <= 0) return null;
  const newIdx = index - 1;
  const key = history[newIdx];
  if (key === undefined) return null;
  return { index: newIdx, key };
}

export function navStepForward(
  history: readonly string[],
  index: number,
): { index: number; key: string } | null {
  if (index >= history.length - 1) return null;
  const newIdx = index + 1;
  const key = history[newIdx];
  if (key === undefined) return null;
  return { index: newIdx, key };
}
