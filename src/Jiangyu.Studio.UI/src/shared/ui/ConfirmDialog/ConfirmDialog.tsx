import { useEffect, useRef } from "react";
import { Modal } from "@shared/ui/Modal/Modal";
import { ModalHeader } from "@shared/ui/Modal/ModalHeader";
import { Button } from "@shared/ui/Button/Button";
import styles from "./ConfirmDialog.module.css";

export interface ConfirmDialogProps {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel?: string;
  readonly cancelLabel?: string;
  readonly onConfirm: () => void;
  readonly onCancel: () => void;
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
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
      <ModalHeader id="confirm-title" title={title} />
      <div className={styles.body}>{message}</div>
      <div className={styles.footer}>
        <Button variant="ghost" onClick={onCancel}>
          {cancelLabel}
        </Button>
        <Button ref={confirmRef} variant="primary" onClick={onConfirm}>
          {confirmLabel}
        </Button>
      </div>
    </Modal>
  );
}
