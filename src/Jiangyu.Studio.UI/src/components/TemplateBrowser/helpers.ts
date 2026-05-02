// Pure helpers extracted from TemplateBrowser.tsx so the JSX module only
// exports React components — keeps Vite fast-refresh working and gives the
// unit tests a stable, side-effect-free import surface.

import type { TemplateInstanceEntry } from "@lib/rpc";

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
