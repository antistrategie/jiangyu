import { useEffect, useRef, useState, useLayoutEffect } from "react";
import { createPortal } from "react-dom";
import { useDismissOnOutsideClick } from "@shared/utils/useDismissOnOutsideClick";
import styles from "./ContextMenu.module.css";

export interface ContextMenuItem {
  readonly label: string;
  readonly shortcut?: string;
  readonly disabled?: boolean;
  readonly onSelect: () => void;
}

export type ContextMenuEntry = ContextMenuItem | "separator";

interface ContextMenuProps {
  x: number;
  y: number;
  items: ContextMenuEntry[];
  onClose: () => void;
}

export function ContextMenu({ x, y, items, onClose }: ContextMenuProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ x, y });

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    let nx = x;
    let ny = y;
    if (nx + rect.width > window.innerWidth) nx = Math.max(4, window.innerWidth - rect.width - 4);
    if (ny + rect.height > window.innerHeight)
      ny = Math.max(4, window.innerHeight - rect.height - 4);
    // eslint-disable-next-line @eslint-react/set-state-in-effect -- legitimate post-layout measurement: read DOM rect, adjust if overflowing viewport. Cannot be derived during render.
    if (nx !== x || ny !== y) setPos({ x: nx, y: ny });
  }, [x, y]);

  useDismissOnOutsideClick(ref, onClose, { dismissOnScroll: true, dismissOnBlur: true });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
    };
  }, [onClose]);

  return createPortal(
    <div ref={ref} className={styles.menu} style={{ left: pos.x, top: pos.y }}>
      {/* Index keys are safe here: a context menu's items list is built once
          per open and doesn't reorder. Separators have no stable identity, so
          positional index is the natural key. */}
      {items.map((item, i) =>
        item === "separator" ? (
          // eslint-disable-next-line @eslint-react/no-array-index-key -- see comment above; menu items don't reorder during the menu's lifetime.
          <div key={`sep-${i}`} className={styles.separator} />
        ) : (
          <button
            // eslint-disable-next-line @eslint-react/no-array-index-key -- see comment above; menu items don't reorder during the menu's lifetime.
            key={`${item.label}-${i}`}
            className={styles.item}
            type="button"
            disabled={item.disabled}
            onClick={() => {
              item.onSelect();
              onClose();
            }}
          >
            <span className={styles.label}>{item.label}</span>
            {item.shortcut && <span className={styles.shortcut}>{item.shortcut}</span>}
          </button>
        ),
      )}
    </div>,
    document.body,
  );
}
