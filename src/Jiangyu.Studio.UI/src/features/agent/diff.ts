/**
 * Line-level unified diff. Splits both texts on `\n`, finds the longest
 * common subsequence of lines via the standard O(n·m) LCS dynamic-programming
 * recurrence, then walks the matrix to emit one `DiffLine` per line in either
 * input. Renders the same shape as `git diff --no-color -U∞` but
 * representation-only — no hunk headers, no @@ markers, no context windowing.
 *
 * Quadratic time/space is fine for the per-tool-call budget (typical agent
 * edits are dozens of lines, max a few hundred). For pathological inputs
 * (e.g. agent rewrites a 50k-line generated file), the caller should clamp
 * via `truncateForDisplay` before computing.
 */
export type DiffLineKind = "context" | "added" | "removed";

export interface DiffLine {
  readonly kind: DiffLineKind;
  readonly text: string;
}

export function computeLineDiff(oldText: string, newText: string): DiffLine[] {
  const oldLines = oldText.length === 0 ? [] : oldText.split("\n");
  const newLines = newText.length === 0 ? [] : newText.split("\n");

  if (oldLines.length === 0) return newLines.map((text) => ({ kind: "added", text }));
  if (newLines.length === 0) return oldLines.map((text) => ({ kind: "removed", text }));

  // LCS DP table flattened to 1D so the lint's noUncheckedIndexedAccess
  // doesn't require non-null asserts on every cell read.
  // lcs[i * stride + j] = length of LCS of oldLines[0..i] and newLines[0..j].
  const m = oldLines.length;
  const n = newLines.length;
  const stride = n + 1;
  const lcs = new Int32Array((m + 1) * stride);
  // get/set wrappers because TS treats indexed reads as `number | undefined`
  // under noUncheckedIndexedAccess; the typed array can't actually return
  // undefined inside the bounds we use here. Same idea for old/newLines —
  // the loop bounds keep us inside [0, length).
  const get = (idx: number): number => lcs[idx] ?? 0;
  const oldAt = (idx: number): string => oldLines[idx] ?? "";
  const newAt = (idx: number): string => newLines[idx] ?? "";
  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      if (oldAt(i - 1) === newAt(j - 1)) {
        lcs[i * stride + j] = get((i - 1) * stride + (j - 1)) + 1;
      } else {
        lcs[i * stride + j] = Math.max(get((i - 1) * stride + j), get(i * stride + (j - 1)));
      }
    }
  }

  // Walk back from (m, n) collecting lines. Lines on the LCS diagonal are
  // context; off-diagonal lines are added (column move) or removed (row
  // move). We push during the back-walk and reverse at the end. Tie-break:
  // when both moves preserve the same LCS length, prefer the column move
  // (added). Pushing added BEFORE removed in back-walk order = added AFTER
  // removed in the final output, which matches `git diff`'s "− then +"
  // convention.
  const out: DiffLine[] = [];
  let i = m;
  let j = n;
  while (i > 0 && j > 0) {
    if (oldAt(i - 1) === newAt(j - 1)) {
      out.push({ kind: "context", text: oldAt(i - 1) });
      i--;
      j--;
    } else if (get((i - 1) * stride + j) > get(i * stride + (j - 1))) {
      out.push({ kind: "removed", text: oldAt(i - 1) });
      i--;
    } else {
      out.push({ kind: "added", text: newAt(j - 1) });
      j--;
    }
  }
  while (i > 0) {
    out.push({ kind: "removed", text: oldAt(i - 1) });
    i--;
  }
  while (j > 0) {
    out.push({ kind: "added", text: newAt(j - 1) });
    j--;
  }

  out.reverse();
  return out;
}

/**
 * Counts of non-context lines in a diff, suitable for a "+12 −3" summary
 * badge.
 */
export interface DiffStats {
  readonly added: number;
  readonly removed: number;
}

export function diffStats(lines: readonly DiffLine[]): DiffStats {
  let added = 0;
  let removed = 0;
  for (const line of lines) {
    if (line.kind === "added") added++;
    else if (line.kind === "removed") removed++;
  }
  return { added, removed };
}
