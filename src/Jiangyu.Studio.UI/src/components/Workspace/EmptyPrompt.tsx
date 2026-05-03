import { BROWSER_KIND_META, type BrowserPane } from "@lib/layout";
import { useAiEnabled } from "@lib/settings";
import styles from "./Workspace.module.css";

interface EmptyPromptProps {
  onOpenBrowser: (kind: BrowserPane["kind"]) => void;
}

export function EmptyPrompt({ onOpenBrowser }: EmptyPromptProps) {
  const [aiEnabled] = useAiEnabled();
  const kinds = (Object.keys(BROWSER_KIND_META) as BrowserPane["kind"][]).filter(
    (kind) => kind !== "agent" || aiEnabled,
  );

  return (
    <div className={styles.emptyStack}>
      <p className={styles.empty}>Open a file from the sidebar</p>
      <p className={styles.emptySeparator}>or</p>
      <ul className={styles.emptyActions}>
        {kinds.map((kind) => (
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
