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

/**
 * Group actions by scope, preserving the order scopes first appear — except
 * FILES_SCOPE, which is always pinned to the bottom so file results don't
 * break up the more action-like groups above them.
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
  const filesIdx = entries.findIndex(([scope]) => scope === FILES_SCOPE);
  if (filesIdx >= 0 && filesIdx !== entries.length - 1) {
    const [filesEntry] = entries.splice(filesIdx, 1);
    entries.push(filesEntry!);
  }
  return entries;
}
