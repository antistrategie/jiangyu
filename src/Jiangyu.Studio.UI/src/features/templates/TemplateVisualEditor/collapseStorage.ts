import { loadJson, removeKey, saveJson } from "@shared/storage";
import type { EditorDirective, EditorNode } from "./types";

const STORAGE_PREFIX = "jiangyu:visualEditor:collapsed:";
const COMPOSITE_STORAGE_PREFIX = "jiangyu:visualEditor:compositeCollapsed:";

function isArray(value: unknown): value is unknown[] {
  return Array.isArray(value);
}

function isStringBoolEntry(entry: unknown): entry is [string, boolean] {
  return (
    Array.isArray(entry) &&
    entry.length === 2 &&
    typeof entry[0] === "string" &&
    typeof entry[1] === "boolean"
  );
}

/**
 * Stable per-node identity used to key collapse state across parses.
 * `_uiId` regenerates on every parse, so it can't be used; instead derive
 * a key from the node's structural identity (kind + templateType + the
 * identity field for that kind). Occurrence index disambiguates the rare
 * case of duplicate keys within one file (e.g. two patches targeting the
 * same template, or two unnamed in-progress cards).
 */
export function computeNodeKeyByUiId(nodes: readonly EditorNode[]): Map<string, string> {
  const result = new Map<string, string>();
  const counts = new Map<string, number>();
  for (const n of nodes) {
    const idPart = n.kind === "Patch" ? (n.templateId ?? "") : (n.cloneId ?? "");
    const base = `${n.kind}:${n.templateType}:${idPart}`;
    const occ = counts.get(base) ?? 0;
    counts.set(base, occ + 1);
    if (n._uiId !== undefined) result.set(n._uiId, `${base}#${occ}`);
  }
  return result;
}

export function loadCollapsed(filePath: string): Set<string> {
  if (!filePath) return new Set();
  // Permissive filter: drop non-string entries instead of failing the whole
  // load, so a stale entry from an older serialiser shape doesn't wipe the
  // user's collapse state.
  const parsed = loadJson(STORAGE_PREFIX + filePath, isArray);
  if (parsed === null) return new Set();
  return new Set(parsed.filter((x): x is string => typeof x === "string"));
}

export function saveCollapsed(filePath: string, keys: ReadonlySet<string>): void {
  if (!filePath) return;
  if (keys.size === 0) removeKey(STORAGE_PREFIX + filePath);
  else saveJson(STORAGE_PREFIX + filePath, [...keys]);
}

/** Drop entries from `stored` that don't match any current node key. */
export function pruneCollapsed(
  stored: ReadonlySet<string>,
  currentKeys: Iterable<string>,
): Set<string> {
  const valid = new Set(currentKeys);
  const next = new Set<string>();
  for (const k of stored) if (valid.has(k)) next.add(k);
  return next;
}

/**
 * Stable per-composite identity used to key collapse state across parses.
 * The directive's `_uiId` regenerates on every parse, so we derive a
 * positional path key — `${nodeKey}/dir[i]/dir[j]/...` — that survives
 * round-trips through the parser. Order-dependent: reordering composites
 * shifts indices and loses persisted state for those positions. Auto-
 * generated content (voicelines.kdl) has stable order; hand-authored
 * content may drop collapse state on structural edits, which we accept.
 */
export function computeCompositeKeyByUiId(
  nodes: readonly EditorNode[],
  nodeKeyByUiId: ReadonlyMap<string, string>,
): Map<string, string> {
  const result = new Map<string, string>();

  function walk(directives: readonly EditorDirective[], parentKey: string): void {
    directives.forEach((d, i) => {
      const v = d.value;
      if (!v || (v.kind !== "Composite" && v.kind !== "TypeConstruction")) return;
      const key = `${parentKey}/dir[${i}]`;
      if (d._uiId !== undefined) result.set(d._uiId, key);
      const nested = v.compositeDirectives ?? [];
      walk(nested, key);
    });
  }

  for (const node of nodes) {
    const nodeKey = node._uiId !== undefined ? nodeKeyByUiId.get(node._uiId) : undefined;
    if (nodeKey === undefined) continue;
    walk(node.directives, nodeKey);
  }
  return result;
}

/**
 * Composite-collapse storage uses a Map<stableKey, explicitState> because
 * the default state depends on content (collapsed when populated, expanded
 * when empty). A presence-only Set would conflate "default" with
 * "explicitly expanded". Persisted entries always reflect a deliberate
 * user toggle.
 */
export function loadCompositeCollapse(filePath: string): Map<string, boolean> {
  if (!filePath) return new Map();
  // Permissive filter: keep valid [string, boolean] pairs, drop the rest.
  const parsed = loadJson(COMPOSITE_STORAGE_PREFIX + filePath, isArray);
  if (parsed === null) return new Map();
  return new Map(parsed.filter(isStringBoolEntry));
}

export function saveCompositeCollapse(filePath: string, map: ReadonlyMap<string, boolean>): void {
  if (!filePath) return;
  if (map.size === 0) removeKey(COMPOSITE_STORAGE_PREFIX + filePath);
  else saveJson(COMPOSITE_STORAGE_PREFIX + filePath, [...map.entries()]);
}

export function pruneCompositeCollapse(
  stored: ReadonlyMap<string, boolean>,
  currentKeys: Iterable<string>,
): Map<string, boolean> {
  const valid = new Set(currentKeys);
  const next = new Map<string, boolean>();
  for (const [k, v] of stored) if (valid.has(k)) next.set(k, v);
  return next;
}
