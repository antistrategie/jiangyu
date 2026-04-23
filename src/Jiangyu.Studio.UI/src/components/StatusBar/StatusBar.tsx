import { useEffect, useState } from "react";
import { formatDurationShort, type CompileState, type CompileStatus } from "../../lib/compile.ts";
import styles from "./StatusBar.module.css";

interface StatusBarProps {
  readonly compileState: CompileState;
  readonly onOpenCompileModal: () => void;
}

/**
 * Thin bottom bar showing compile status inline.
 * Success auto-clears after a few seconds; failure persists until the next
 * compile or until the user clicks to open the dossier.
 */
export function StatusBar({ compileState, onOpenCompileModal }: StatusBarProps) {
  const displayStatus = useDisplayStatus(compileState);
  useTickerWhileRunning(compileState.status === "running");
  useStatusBarHeight();

  const progressPct = computeProgressPct(compileState);

  const barClass =
    compileState.status === "running" ? `${styles.bar} ${styles.barRunning}` : styles.bar;

  return (
    <div className={barClass}>
      {compileState.status === "running" && (
        <div className={styles.progressTrack}>
          <div
            className={styles.progressFill}
            style={{ width: progressPct !== null ? `${progressPct}%` : "0%" }}
          />
        </div>
      )}

      <button
        type="button"
        className={styles.compileRegion}
        onClick={onOpenCompileModal}
        title="Open compile dossier"
      >
        {displayStatus === "running" && <RunningSegment state={compileState} />}
        {displayStatus === "success" && <SuccessSegment state={compileState} />}
        {displayStatus === "failed" && <FailedSegment state={compileState} />}
        {displayStatus === "idle" && <IdleSegment />}
      </button>

      <span className={styles.spacer} />
      <span className={styles.kbd}>Ctrl+Shift+B — compile</span>
    </div>
  );
}

function RunningSegment({ state }: { state: CompileState }) {
  return (
    <>
      {state.phase !== null && <span className={styles.phase}>{state.phase}</span>}
      {state.phase !== null && state.statusLine !== null && (
        <span className={styles.separator}>·</span>
      )}
      {state.statusLine !== null && <span className={styles.statusLine}>{state.statusLine}</span>}
      {state.phase === null && state.statusLine === null && (
        <span className={styles.phase}>Compiling…</span>
      )}
      <span className={styles.separator}>·</span>
      <span className={styles.duration}>{formatDurationLive(state)}</span>
    </>
  );
}

function SuccessSegment({ state }: { state: CompileState }) {
  const duration =
    state.startedAt !== null && state.finishedAt !== null
      ? formatDurationShort(state.finishedAt - state.startedAt)
      : null;
  return (
    <span className={styles.success}>
      Compile complete{duration !== null ? ` · ${duration}` : ""}
    </span>
  );
}

function FailedSegment({ state }: { state: CompileState }) {
  return (
    <span className={styles.failed}>
      Compile failed{state.errorMessage !== null ? ` — ${state.errorMessage}` : ""}
    </span>
  );
}

function IdleSegment() {
  return <span className={styles.idle}>Ready</span>;
}

// ---------------------------------------------------------------------------
// Hooks
// ---------------------------------------------------------------------------

/** Force a re-render every 500ms while running so the duration ticks. */
function useTickerWhileRunning(running: boolean): void {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!running) return;
    const handle = setInterval(() => setTick((n) => n + 1), 500);
    return () => clearInterval(handle);
  }, [running]);
}

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

/**
 * Tracks what the status bar should *display*. Success auto-clears after
 * SUCCESS_LINGER_MS; failure persists until the next compile.
 * Does NOT touch the shared CompileState.
 */
function useDisplayStatus(state: CompileState): CompileStatus {
  const [override, setOverride] = useState<"idle" | null>(null);

  useEffect(() => {
    setOverride(null);
  }, [state.status]);

  useEffect(() => {
    if (state.status !== "success") return;
    const handle = setTimeout(() => setOverride("idle"), SUCCESS_LINGER_MS);
    return () => clearTimeout(handle);
  }, [state.status, state.finishedAt]);

  if (override !== null) return override;
  return state.status;
}

/** Set --status-bar-h on :root while the status bar is mounted. */
function useStatusBarHeight(): void {
  useEffect(() => {
    document.documentElement.style.setProperty("--status-bar-h", "27px");
    return () => {
      document.documentElement.style.setProperty("--status-bar-h", "0px");
    };
  }, []);
}

export function formatDurationLive(state: CompileState): string {
  if (state.startedAt === null) return "0s";
  const end = state.finishedAt ?? Date.now();
  return formatDurationShort(end - state.startedAt);
}
