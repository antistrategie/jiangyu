import type { EditorNode } from "./types";

const STORAGE_PREFIX = "jiangyu:visualEditor:collapsed:";

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
