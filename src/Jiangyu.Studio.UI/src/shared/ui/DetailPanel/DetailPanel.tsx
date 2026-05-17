import type { ReactNode } from "react";
import styles from "./DetailPanel.module.css";

export function DetailTitle({ children }: { children: ReactNode }) {
  return <div className={styles.title}>{children}</div>;
}

export function MetaBlock({ children }: { children: ReactNode }) {
  return <div className={styles.meta}>{children}</div>;
}

export function MetaRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className={styles.metaRow}>
      <span className={styles.metaLabel}>{label}</span>
      <span className={styles.metaValue}>{value}</span>
    </div>
  );
}

export function SectionHeader({ children }: { children: ReactNode }) {
  return <div className={styles.sectionHeader}>{children}</div>;
}
