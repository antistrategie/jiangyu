import { GitBranch } from "lucide-react";
import styles from "./Topbar.module.css";

interface TopbarProps {
  projectName: string;
  /** Current git branch for the open project, or null when git isn't
   *  available, the project isn't a git repo, or HEAD is detached. */
  gitBranch: string | null;
  onOpenPalette: () => void;
}

export function Topbar({ projectName, gitBranch, onOpenPalette }: TopbarProps) {
  return (
    <header className={styles.topbar}>
      <div className={styles.wordmark}>
        <div className={styles.wordmarkText}>
          <span className={styles.cjk}>绛雨</span>
        </div>
      </div>
      <span className={styles.separator} />
      <span className={styles.breadcrumb}>{projectName}</span>
      {gitBranch !== null && (
        <span className={styles.branch} title={`Git branch: ${gitBranch}`}>
          <GitBranch size={12} />
          {gitBranch}
        </span>
      )}
      <div className={styles.spacer} />
      <button className={styles.palette} type="button" onClick={onOpenPalette}>
        <span className={styles.paletteIcon}>⌘</span>
        <span>Search files, commands…</span>
        <kbd className={styles.kbd}>Ctrl+Shift+P</kbd>
      </button>
    </header>
  );
}
