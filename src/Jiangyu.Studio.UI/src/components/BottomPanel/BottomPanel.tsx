import styles from "./BottomPanel.module.css";

export function BottomPanel() {
  return (
    <div className={styles.wrapper}>
      <div className={styles.panel}>
        <div className={styles.header}>
          <span className={styles.headerCjk}>输出</span>
          <span className={styles.headerEn}>Output</span>
        </div>
        <div className={styles.content}>
          <pre className={styles.output}>Jiangyu Studio ready.</pre>
        </div>
      </div>
      <footer className={styles.statusbar}>
        <span className={styles.statusOk}>● ready</span>
        <span className={styles.statusMuted}>UTF-8</span>
        <span className={styles.statusMuted}>LF</span>
        <span className={styles.statusRight}>⌘K commands</span>
      </footer>
    </div>
  );
}
