import { useState, useRef, useEffect } from "react";
import { Modal } from "@shared/ui/Modal/Modal";
import { rpcCall } from "@shared/rpc";
import styles from "./NewProjectDialog.module.css";

export interface NewProjectDialogProps {
  readonly onCreated: (path: string) => void;
  readonly onCancel: () => void;
}

export function NewProjectDialog({ onCreated, onCancel }: NewProjectDialogProps) {
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  const valid = name.trim().length > 0;

  const handleCreate = async () => {
    const trimmed = name.trim();
    if (!trimmed || busy) return;

    setBusy(true);
    setError(null);

    try {
      const result = await rpcCall<string | null>("newProject", { name: trimmed });
      if (result !== null) {
        onCreated(result);
      } else {
        setBusy(false);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setBusy(false);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter" && valid && !busy) {
      e.preventDefault();
      void handleCreate();
    }
  };

  return (
    <Modal onClose={onCancel} ariaLabelledBy="new-project-title">
      <div className={styles.dialog}>
        <div id="new-project-title" className={styles.header}>
          New Project · 新项目
        </div>
        <div className={styles.body}>
          <label className={styles.label} htmlFor="new-project-name">
            Project Name
          </label>
          <input
            ref={inputRef}
            id="new-project-name"
            className={styles.input}
            type="text"
            value={name}
            placeholder="MyMod"
            onChange={(e) => setName(e.target.value)}
            onKeyDown={handleKeyDown}
            disabled={busy}
          />
          {error !== null && <div className={styles.error}>{error}</div>}
          <p className={styles.hint}>
            Choose a location in the next step. A folder with this name will be created.
          </p>
        </div>
        <div className={styles.footer}>
          <button type="button" className={styles.btn} onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className={`${styles.btn} ${styles.btnPrimary}`}
            onClick={() => void handleCreate()}
            disabled={!valid || busy}
          >
            {busy ? "Creating…" : "Create"}
          </button>
        </div>
      </div>
    </Modal>
  );
}
