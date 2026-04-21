import Fuse, { type IFuseOptions } from "fuse.js";
import { PALETTE_SCOPE, type PaletteAction } from "../../lib/actions.tsx";

export const FILES_SCOPE = PALETTE_SCOPE.GoToFile;
export const MAX_RESULTS = 60;

export const fuseOptions: IFuseOptions<PaletteAction> = {
  keys: [
    { name: "label", weight: 2 },
    { name: "cn", weight: 1.2 },
    { name: "desc", weight: 0.8 },
    { name: "scope", weight: 0.3 },
  ],
  threshold: 0.4,
  ignoreLocation: true,
};

export function buildFuse(actions: readonly PaletteAction[]): Fuse<PaletteAction> {
  return new Fuse(actions, fuseOptions);
}

/**
 * Filter actions for display. Empty query returns command scopes only
 * (file entries are withheld until the user types, since they can number
 * in the thousands and would drown the list).
 */
export function filterActions(
  query: string,
  actions: readonly PaletteAction[],
  fuse: Fuse<PaletteAction>,
): readonly PaletteAction[] {
  if (query.length === 0) {
    return actions.filter((a) => a.scope !== FILES_SCOPE);
  }
  return fuse.search(query, { limit: MAX_RESULTS }).map((r) => r.item);
}

// Known scopes are ordered explicitly so the palette layout doesn't depend on
// React effect timing (parent vs. child registration order). Unknown scopes
// keep their first-seen order between the known head and the FILES_SCOPE tail.
const SCOPE_ORDER: readonly string[] = [
  PALETTE_SCOPE.Project,
  PALETTE_SCOPE.View,
  PALETTE_SCOPE.File,
  PALETTE_SCOPE.Editor,
];

/**
 * Group actions by scope. Known scopes follow `SCOPE_ORDER`; unknown scopes
 * trail in first-seen order; FILES_SCOPE is always pinned to the bottom so
 * file results don't break up the action groups above them.
 */
export function groupByScope(
  actions: readonly PaletteAction[],
): readonly (readonly [string, readonly PaletteAction[]])[] {
  const map = new Map<string, PaletteAction[]>();
  for (const a of actions) {
    let bucket = map.get(a.scope);
    if (!bucket) {
      bucket = [];
      map.set(a.scope, bucket);
    }
    bucket.push(a);
  }
  const entries = [...map.entries()];
  entries.sort(([a], [b]) => scopeRank(a) - scopeRank(b));
  return entries;
}

function scopeRank(scope: string): number {
  if (scope === FILES_SCOPE) return 1_000_000;
  const idx = SCOPE_ORDER.indexOf(scope);
  return idx >= 0 ? idx : 1000;
}
