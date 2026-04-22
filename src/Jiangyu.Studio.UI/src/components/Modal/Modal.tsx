import { useEffect, type ReactNode } from "react";
import { createPortal } from "react-dom";
import styles from "./Modal.module.css";

/**
 * Shared chrome for centred, scrim-backed dialogs. Owns the portal, backdrop
 * click-to-close, and Escape-to-close behaviour; callers render their own
 * inner dialog (sizing, header, footer) as `children`. Floating surfaces that
 * aren't scrim-backed (palette, context menu) don't use this.
 */
interface ModalProps {
  readonly onClose: () => void;
  readonly ariaLabelledBy?: string;
  readonly children: ReactNode;
}

export function Modal({ onClose, ariaLabelledBy, children }: ModalProps) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return createPortal(
    <div
      className={styles.backdrop}
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
      role="dialog"
      aria-modal="true"
      aria-labelledby={ariaLabelledBy}
    >
      {children}
    </div>,
    document.body,
  );
}
