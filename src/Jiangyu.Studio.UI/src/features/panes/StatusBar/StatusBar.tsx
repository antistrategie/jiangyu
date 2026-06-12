import { useEffect, useReducer, useState } from "react";
import { CheckCircle, CircleDot, PanelLeft, PanelLeftClose, XCircle } from "lucide-react";
import { formatDurationShort, useCompileStore, type CompileStatus } from "@features/compile";
import { useSidebarHidden } from "@features/settings/settings";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { SUCCESS_LINGER_MS, computeProgressPct, formatDurationLive } from "./statusBarHelpers";
import styles from "./StatusBar.module.css";

interface StatusBarProps {
  readonly onOpenCompileModal: () => void;
}

/**
 * Thin bottom bar showing compile status inline.
 * Success auto-clears after a few seconds; failure persists until the next
 * compile or until the user clicks to open the dossier.
 */
export function StatusBar({ onOpenCompileModal }: StatusBarProps) {
  const status = useCompileStore((s) => s.status);
  const progress = useCompileStore((s) => s.progress);
  const finishedAt = useCompileStore((s) => s.finishedAt);

  const displayStatus = useDisplayStatus(status, finishedAt);
  useTickerWhileRunning(status === "running");
  useStatusBarHeight();
  const [sidebarHidden, setSidebarHidden] = useSidebarHidden();

  const progressPct = computeProgressPct(status, progress);

  const barClass = status === "running" ? `${styles.bar} ${styles.barRunning}` : styles.bar;

  return (
    <div className={barClass}>
      {status === "running" && (
        <div className={styles.progressTrack}>
          <div
            className={styles.progressFill}
            style={{ width: progressPct !== null ? `${progressPct}%` : "0%" }}
          />
        </div>
      )}

      <button
        type="button"
        className={styles.sidebarToggle}
        onClick={() => setSidebarHidden(!sidebarHidden)}
        aria-label={sidebarHidden ? "Show sidebar" : "Hide sidebar"}
        aria-pressed={!sidebarHidden}
        title={sidebarHidden ? "Show sidebar (Ctrl+B)" : "Hide sidebar (Ctrl+B)"}
      >
        {sidebarHidden ? <PanelLeft size={12} /> : <PanelLeftClose size={12} />}
      </button>

      <span className={styles.vrule} aria-hidden="true" />

      <button
        type="button"
        className={styles.compileRegion}
        onClick={onOpenCompileModal}
        title="Open compile dossier"
      >
        <StatusIcon status={displayStatus} />
        {displayStatus === "running" && <RunningSegment />}
        {displayStatus === "success" && <SuccessSegment />}
        {displayStatus === "failed" && <FailedSegment />}
        {displayStatus === "idle" && <IdleSegment />}
      </button>
    </div>
  );
}

function StatusIcon({ status }: { status: CompileStatus }) {
  if (status === "running") {
    // Size matches the lucide 12px icons so all status states share a
    // visual footprint. Colours come from the Spinner's currentColor-aware
    // border, tuned for the bar's dark surface.
    return (
      <Spinner
        size={12}
        trackColor="var(--ink-2)"
        accentColor="var(--paper-0)"
        className={styles.statusSpinner}
      />
    );
  }
  if (status === "success") {
    return <CheckCircle size={12} className={styles.statusIconSuccess} />;
  }
  if (status === "failed") {
    return <XCircle size={12} className={styles.statusIconFailed} />;
  }
  return <CircleDot size={12} className={styles.statusIconIdle} />;
}

function RunningSegment() {
  const phase = useCompileStore((s) => s.phase);
  const statusLine = useCompileStore((s) => s.statusLine);
  const startedAt = useCompileStore((s) => s.startedAt);
  const finishedAt = useCompileStore((s) => s.finishedAt);
  return (
    <>
      {phase !== null && <span className={styles.phase}>{phase}</span>}
      {phase !== null && statusLine !== null && <span className={styles.separator}>·</span>}
      {statusLine !== null && <span className={styles.statusLine}>{statusLine}</span>}
      {phase === null && statusLine === null && <span className={styles.phase}>Compiling…</span>}
      <span className={styles.separator}>·</span>
      <span className={styles.duration}>{formatDurationLive(startedAt, finishedAt)}</span>
    </>
  );
}

function SuccessSegment() {
  const startedAt = useCompileStore((s) => s.startedAt);
  const finishedAt = useCompileStore((s) => s.finishedAt);
  const duration =
    startedAt !== null && finishedAt !== null ? formatDurationShort(finishedAt - startedAt) : null;
  return (
    <span className={styles.success}>
      Compile complete{duration !== null ? ` · ${duration}` : ""}
    </span>
  );
}

function FailedSegment() {
  const errorMessage = useCompileStore((s) => s.errorMessage);
  return (
    <span className={styles.failed}>
      Compile failed{errorMessage !== null ? ` — ${errorMessage}` : ""}
    </span>
  );
}

function IdleSegment() {
  return (
    <>
      <span className={styles.idle}>Ready</span>
      <span className={styles.kbd}>(Ctrl+Shift+B — compile)</span>
    </>
  );
}

// ---------------------------------------------------------------------------
// Hooks
// ---------------------------------------------------------------------------

/** Force a re-render every 500ms while running so the duration ticks. */
function useTickerWhileRunning(running: boolean): void {
  const [, forceUpdate] = useReducer((n: number) => n + 1, 0);
  useEffect(() => {
    if (!running) return;
    const handle = setInterval(forceUpdate, 500);
    return () => clearInterval(handle);
  }, [running]);
}

/**
 * Tracks what the status bar should *display*. Success auto-clears after
 * SUCCESS_LINGER_MS; failure persists until the next compile.
 * Does NOT touch the shared compile state.
 */
function useDisplayStatus(status: CompileStatus, finishedAt: number | null): CompileStatus {
  const [override, setOverride] = useState<"idle" | null>(null);

  const [prevStatus, setPrevStatus] = useState(status);
  if (prevStatus !== status) {
    setPrevStatus(status);
    setOverride(null);
  }

  useEffect(() => {
    if (status !== "success") return;
    const handle = setTimeout(() => setOverride("idle"), SUCCESS_LINGER_MS);
    return () => clearTimeout(handle);
  }, [status, finishedAt]);

  if (override !== null) return override;
  return status;
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
