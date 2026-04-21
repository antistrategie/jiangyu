import styles from "./Topbar.module.css";

interface TopbarProps {
  projectName: string | null | undefined;
  onOpenPalette: () => void;
}

export function Topbar({ projectName, onOpenPalette }: TopbarProps) {
  return (
    <header className={styles.topbar}>
      <div className={styles.wordmark}>
        <div className={styles.wordmarkText}>
          <span className={styles.cjk}>绛雨</span>
        </div>
      </div>
      <span className={styles.separator} />
      <span className={styles.breadcrumb}>{projectName ?? "No project"}</span>
      <div className={styles.spacer} />
      <button className={styles.palette} type="button" onClick={onOpenPalette}>
        <span className={styles.paletteIcon}>⌘</span>
        <span>Search files, commands…</span>
        <kbd className={styles.kbd}>Ctrl+Shift+P</kbd>
      </button>
    </header>
  );
}
