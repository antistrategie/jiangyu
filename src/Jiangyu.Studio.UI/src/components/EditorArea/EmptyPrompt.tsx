import { BROWSER_KIND_META, type BrowserPane } from "../../lib/layout.ts";
import styles from "./EditorArea.module.css";

interface EmptyPromptProps {
  onOpenBrowser: (kind: BrowserPane["kind"]) => void;
}

export function EmptyPrompt({ onOpenBrowser }: EmptyPromptProps) {
  return (
    <div className={styles.emptyStack}>
      <p className={styles.empty}>Open a file from the sidebar</p>
      <p className={styles.emptySeparator}>or</p>
      <ul className={styles.emptyActions}>
        {(Object.keys(BROWSER_KIND_META) as BrowserPane["kind"][]).map((kind) => (
          <li key={kind}>
            <button
              type="button"
              className={styles.emptyAction}
              onClick={() => onOpenBrowser(kind)}
            >
              Open {BROWSER_KIND_META[kind].label}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
