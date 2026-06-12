import styles from "./AssetBrowser.module.css";

export interface BatchProgress {
  readonly done: number;
  readonly total: number;
}

interface BatchProgressBarProps {
  readonly progress: BatchProgress | null;
  readonly active: boolean;
}

/**
 * Determinate bar for the bulk export/import flows. Hidden unless a
 * multi-row batch is running: single-row batches finish too fast for the
 * bar to communicate anything.
 */
export function BatchProgressBar({ progress, active }: BatchProgressBarProps) {
  if (!active || progress === null || progress.total <= 1) return null;
  return (
    <div className={styles.exportProgress}>
      <div
        className={styles.exportProgressFill}
        style={{ width: `${(progress.done / progress.total) * 100}%` }}
      />
    </div>
  );
}
