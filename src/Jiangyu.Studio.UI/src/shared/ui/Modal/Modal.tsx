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
    // Outer scrim is exposed to assistive tech as a "Close" button (Escape
    // also closes via the global keydown above). The inner wrapper carries
    // the dialog role + aria-modal so screen readers see the dialog as a
    // single element regardless of children layout.
    <div
      className={styles.backdrop}
      role="button"
      tabIndex={-1}
      aria-label="Close dialog"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        className={styles.dialogContent}
        role="dialog"
        aria-modal="true"
        aria-labelledby={ariaLabelledBy}
      >
        {children}
      </div>
    </div>,
    document.body,
  );
}
