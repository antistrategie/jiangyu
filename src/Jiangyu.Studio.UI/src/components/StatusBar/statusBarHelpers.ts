import {
  formatDurationShort,
  type CompileState,
  type CompileStatus,
} from "@lib/compile/compile.ts";

export const SUCCESS_LINGER_MS = 5_000;

/**
 * Compute the progress bar percentage from compile state.
 * Returns null when there is no meaningful progress to show.
 */
export function computeProgressPct(state: CompileState): number | null {
  if (state.status !== "running" || state.progress === null || state.progress.total <= 0) {
    return null;
  }
  return Math.round((state.progress.current / state.progress.total) * 100);
}

/**
 * Pure derivation of what the status bar should display given the underlying
 * compile status and whether a display-override is active.
 */
export function deriveDisplayStatus(status: CompileStatus, override: "idle" | null): CompileStatus {
  if (override !== null) return override;
  return status;
}

export function formatDurationLive(state: CompileState): string {
  if (state.startedAt === null) return "0s";
  const end = state.finishedAt ?? Date.now();
  return formatDurationShort(end - state.startedAt);
}
