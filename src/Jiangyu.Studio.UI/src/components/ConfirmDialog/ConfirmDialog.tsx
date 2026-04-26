import { useEffect, useRef } from "react";
import { Modal } from "@components/Modal/Modal";
import styles from "./ConfirmDialog.module.css";

export type ConfirmVariant = "default" | "danger";

export interface ConfirmDialogProps {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel?: string;
  readonly cancelLabel?: string;
  readonly variant?: ConfirmVariant;
  readonly onConfirm: () => void;
  readonly onCancel: () => void;
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  variant = "default",
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const confirmRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    confirmRef.current?.focus();
  }, []);

  // Modal handles Escape → onCancel; Enter-to-confirm is specific to this
  // dialog's semantics, so it stays here.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Enter") {
        e.preventDefault();
        onConfirm();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onConfirm]);

  return (
    <Modal onClose={onCancel} ariaLabelledBy="confirm-title">
      <div className={styles.dialog}>
        <div id="confirm-title" className={styles.header}>
          {title}
        </div>
        <div className={styles.body}>{message}</div>
        <div className={styles.footer}>
          <button type="button" className={styles.btn} onClick={onCancel}>
            {cancelLabel}
          </button>
          <button
            ref={confirmRef}
            type="button"
            className={`${styles.btn} ${variant === "danger" ? styles.btnDanger : styles.btnPrimary}`}
            onClick={onConfirm}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </Modal>
  );
}
