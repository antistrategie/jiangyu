// Pure helpers for the matrix grid editor. Lives in its own file so
// MatrixEditor.tsx stays a components-only module (Vite fast-refresh
// requires that — mixing component exports with pure-value exports
// disables HMR on the whole file).

import type { EnumMemberEntry } from "@lib/rpc";

/**
 * [Flags] heuristic: every non-zero member is a single power of two,
 * and there are at least two such bits. The catalog doesn't surface
 * the FlagsAttribute over the wire today, so we infer it from the
 * value shape; standard-library [Flags] enums always satisfy this.
 */
export function isFlagsEnum(members: readonly EnumMemberEntry[]): boolean {
  let powerOfTwoCount = 0;
  for (const m of members) {
    if (m.value === 0) continue;
    if (m.value < 0) return false;
    if ((m.value & (m.value - 1)) !== 0) return false;
    powerOfTwoCount++;
  }
  return powerOfTwoCount >= 2;
}

/** Matches a trailing `[]`, `[,]`, `[,,]`, etc. */
export function stripArraySuffix(typeName: string): string {
  return typeName.replace(/\[,*\]$/, "");
}

/**
 * Compact glyph for a flags-cell's bitmask. Single-glyph hex through
 * 0xF; decimal for the larger spans (e.g. 255 for a fully-set
 * ChunkTileFlags byte). Returns "·" for the zero/None case so an
 * empty cell stays distinguishable from a 0-numeric one in the grid.
 */
export function formatFlagsLabel(value: number): string {
  if (value === 0) return "·";
  return value <= 0xf ? value.toString(16).toUpperCase() : value.toString();
}

/**
 * Tooltip text for a flags-cell: lists the bit names making up the
 * mask, falling back to the numeric value when none of the named bits
 * match. Used for native-tooltip strings, so it stays plain-text.
 */
export function formatFlagsTitle(
  value: number,
  members: readonly EnumMemberEntry[],
  r: number,
  c: number,
  isPending: boolean,
): string {
  const coord = `[${r},${c}]${isPending ? " (edited)" : ""}`;
  if (value === 0) {
    const zero = members.find((m) => m.value === 0);
    return `${coord} ${zero ? zero.name : "(none)"}`;
  }
  const names: string[] = [];
  for (const m of members) {
    if (m.value !== 0 && (value & m.value) === m.value) names.push(m.name);
  }
  return `${coord} ${names.length > 0 ? names.join(" | ") : value.toString()}`;
}
