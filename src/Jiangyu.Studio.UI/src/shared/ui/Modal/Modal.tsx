import { useEffect, useRef, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { isTopmostModal, popModal, pushModal } from "./modalStack";
import styles from "./Modal.module.css";

/**
 * Centred, scrim-backed dialog. Owns the portal, backdrop click-to-close,
 * Escape-to-close, focus management (move focus in on open, trap Tab inside,
 * restore the invoker's focus on close), and the dialog chrome (background,
 * hairline, shadow). Callers render the header / body / footer as children.
 * Floating surfaces that aren't scrim-backed (palette, context menu) don't
 * use this.
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

const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function Modal({ onClose, ariaLabelledBy, width = 440, height, children }: ModalProps) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const tokenRef = useRef<symbol | null>(null);
  tokenRef.current ??= Symbol("modal");

  // Focus lifecycle: remember the invoker, move focus into the dialog unless
  // a child effect already claimed it (child effects run before this one, so
  // e.g. ConfirmDialog's confirm-button focus wins), restore on unmount.
  useEffect(() => {
    const token = tokenRef.current;
    if (token !== null) pushModal(token);
    const invoker = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const dialog = dialogRef.current;
    if (dialog !== null && !dialog.contains(document.activeElement)) dialog.focus();
    return () => {
      if (token !== null) popModal(token);
      invoker?.focus();
    };
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // Stacked dialogs each register this listener; only the topmost acts.
      if (tokenRef.current === null || !isTopmostModal(tokenRef.current)) return;
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      if (e.key === "Tab") {
        const dialog = dialogRef.current;
        if (dialog === null) return;
        const focusables = dialog.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR);
        if (focusables.length === 0) {
          e.preventDefault();
          return;
        }
        const first = focusables[0];
        const last = focusables[focusables.length - 1];
        const active = document.activeElement;
        if (e.shiftKey) {
          if (active === first || !dialog.contains(active)) {
            e.preventDefault();
            last?.focus();
          }
        } else if (active === last || !dialog.contains(active)) {
          e.preventDefault();
          first?.focus();
        }
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
        ref={dialogRef}
        className={dialogClass}
        style={dialogStyle}
        role="dialog"
        aria-modal="true"
        aria-labelledby={ariaLabelledBy}
        tabIndex={-1}
      >
        {children}
      </div>
    </div>,
    document.body,
  );
}
