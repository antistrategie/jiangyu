import type { ReactNode } from "react";
import styles from "./EmptyState.module.css";

interface EmptyStateProps {
  readonly title: string;
  /** Single reason renders as one paragraph. An array renders as separate
   *  paragraphs (e.g. a stale-state reason plus a fresh-error reason). */
  readonly reason?: string | readonly string[];
  readonly action?: ReactNode;
}

export function EmptyState({ title, reason, action }: EmptyStateProps) {
  const reasons: readonly string[] =
    reason === undefined ? [] : typeof reason === "string" ? [reason] : reason;
  return (
    <div className={styles.root}>
      <p className={styles.title}>{title}</p>
      {reasons.map((r) => (
        <p key={r} className={styles.reason}>
          {r}
        </p>
      ))}
      {action}
    </div>
  );
}
