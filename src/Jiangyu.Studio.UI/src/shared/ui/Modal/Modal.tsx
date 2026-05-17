import { useEffect, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import styles from "./Modal.module.css";

/**
 * Centred, scrim-backed dialog. Owns the portal, backdrop click-to-close,
 * Escape-to-close, and the dialog chrome (background, hairline, shadow).
 * Callers render the header / body / footer as children. Floating surfaces
 * that aren't scrim-backed (palette, context menu) don't use this.
 */
interface ModalProps {
  readonly onClose: () => void;
  readonly ariaLabelledBy?: string;
  /** Dialog width in pixels. Caps at 92vw on narrow viewports. Default 440. */
  readonly width?: number;
  /** When set, fixes the dialog height (capped at 88vh) so tall dialogs grow
   *  to the requested size rather than hugging their content. */
  readonly height?: number;
  readonly children: ReactNode;
}

export function Modal({ onClose, ariaLabelledBy, width = 440, height, children }: ModalProps) {
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

  // CSS custom properties aren't typed on CSSProperties; cast keeps the
  // assignment honest without an unsafe `any`.
  const dialogStyle = {
    "--modal-width": `${width.toString()}px`,
    ...(height !== undefined ? { "--modal-height": `${height.toString()}px` } : {}),
  } as CSSProperties;
  const dialogClass =
    height === undefined ? styles.dialog : `${styles.dialog} ${styles.dialogTall}`;

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
        className={dialogClass}
        style={dialogStyle}
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
