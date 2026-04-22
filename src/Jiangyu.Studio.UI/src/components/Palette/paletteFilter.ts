import uFuzzy from "@leeoniya/ufuzzy";
import { PALETTE_SCOPE, type PaletteAction } from "../../lib/actions.tsx";

export const FILES_SCOPE = PALETTE_SCOPE.GoToFile;
export const MAX_RESULTS = 60;

export interface PaletteSearchIndex {
  readonly actions: readonly PaletteAction[];
  readonly haystack: string[];
  readonly uf: uFuzzy;
}

// Each action's searchable fields are concatenated into one haystack string,
// with `label` first so position-sensitive scoring ranks label matches highest.
// Empty strings keep the join shape stable when `cn`/`desc` are absent.
function toSearchString(a: PaletteAction): string {
  return `${a.label} ${a.cn ?? ""} ${a.desc ?? ""} ${a.scope}`;
}

export function buildSearchIndex(actions: readonly PaletteAction[]): PaletteSearchIndex {
  return {
    actions,
    haystack: actions.map(toSearchString),
    // interSplit \s+ keeps CJK clusters intact as a single term (default regex
    // [^A-Za-z\d']+ would split on every CJK char). intraMode 1 (SingleError)
    // tolerates a single typo inside a term — matches how palette users search
    // ("formt" → "Format Document"), where MultiInsert would require all chars
    // in order.
    uf: new uFuzzy({ interSplit: "\\s+", intraMode: 1 }),
  };
}

/**
 * Filter actions for display. Empty query returns command scopes only
 * (file entries are withheld until the user types, since they can number
 * in the thousands and would drown the list).
 */
export function filterActions(
  query: string,
  actions: readonly PaletteAction[],
  index: PaletteSearchIndex,
): readonly PaletteAction[] {
  if (query.length === 0) {
    return actions.filter((a) => a.scope !== FILES_SCOPE);
  }
  const [idxs, info, order] = index.uf.search(index.haystack, query);
  if (idxs === null) return [];
  const out: PaletteAction[] = [];
  if (info !== null && order !== null) {
    for (const oi of order) {
      const i = info.idx[oi]!;
      out.push(index.actions[i]!);
      if (out.length >= MAX_RESULTS) break;
    }
  } else {
    for (const i of idxs) {
      out.push(index.actions[i]!);
      if (out.length >= MAX_RESULTS) break;
    }
  }
  return out;
}

// Known scopes follow the declaration order of PALETTE_SCOPE so the palette
// layout doesn't depend on React effect timing (parent vs. child registration
// order). Unknown scopes keep their first-seen order between the known head
// and the FILES_SCOPE tail.
const SCOPE_ORDER: readonly string[] = Object.values(PALETTE_SCOPE);

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
