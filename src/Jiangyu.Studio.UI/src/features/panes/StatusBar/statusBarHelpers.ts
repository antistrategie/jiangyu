import { formatDurationShort, type CompileProgress, type CompileStatus } from "@features/compile";

export const SUCCESS_LINGER_MS = 5_000;

/**
 * Compute the progress bar percentage from compile state.
 * Returns null when there is no meaningful progress to show.
 */
export function computeProgressPct(
  status: CompileStatus,
  progress: CompileProgress | null,
): number | null {
  if (status !== "running" || progress === null || progress.total <= 0) {
    return null;
  }
  return Math.round((progress.current / progress.total) * 100);
}

/**
 * Pure derivation of what the status bar should display given the underlying
 * compile status and whether a display-override is active.
 */
export function deriveDisplayStatus(status: CompileStatus, override: "idle" | null): CompileStatus {
  if (override !== null) return override;
  return status;
}

export function formatDurationLive(startedAt: number | null, finishedAt: number | null): string {
  if (startedAt === null) return "0s";
  const end = finishedAt ?? Date.now();
  return formatDurationShort(end - startedAt);
}
