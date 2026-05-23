import type { EditorDirective, EditorNode } from "./types";

const STORAGE_PREFIX = "jiangyu:visualEditor:collapsed:";
const COMPOSITE_STORAGE_PREFIX = "jiangyu:visualEditor:compositeCollapsed:";

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
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + filePath);
    if (raw === null) return new Set();
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Set();
    return new Set(parsed.filter((x): x is string => typeof x === "string"));
  } catch {
    return new Set();
  }
}

export function saveCollapsed(filePath: string, keys: ReadonlySet<string>): void {
  if (!filePath) return;
  try {
    if (keys.size === 0) {
      localStorage.removeItem(STORAGE_PREFIX + filePath);
    } else {
      localStorage.setItem(STORAGE_PREFIX + filePath, JSON.stringify([...keys]));
    }
  } catch {
    /* quota exceeded / private mode — accept loss */
  }
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
      if (!v || (v.kind !== "Composite" && v.kind !== "HandlerConstruction")) return;
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
  try {
    const raw = localStorage.getItem(COMPOSITE_STORAGE_PREFIX + filePath);
    if (raw === null) return new Map();
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Map();
    const result = new Map<string, boolean>();
    for (const entry of parsed) {
      if (
        Array.isArray(entry) &&
        entry.length === 2 &&
        typeof entry[0] === "string" &&
        typeof entry[1] === "boolean"
      ) {
        result.set(entry[0], entry[1]);
      }
    }
    return result;
  } catch {
    return new Map();
  }
}

export function saveCompositeCollapse(filePath: string, map: ReadonlyMap<string, boolean>): void {
  if (!filePath) return;
  try {
    if (map.size === 0) {
      localStorage.removeItem(COMPOSITE_STORAGE_PREFIX + filePath);
    } else {
      localStorage.setItem(
        COMPOSITE_STORAGE_PREFIX + filePath,
        JSON.stringify(Array.from(map.entries())),
      );
    }
  } catch {
    /* quota exceeded / private mode — accept loss */
  }
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
