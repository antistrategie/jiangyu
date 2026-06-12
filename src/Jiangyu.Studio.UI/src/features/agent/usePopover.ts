import { useCallback, useEffect, useRef, useState } from "react";
import { useDismissOnOutsideClick } from "@shared/utils/useDismissOnOutsideClick";

/**
 * Open/close state for a popover anchored inside a wrapper element, with
 * outside-click and Escape dismissal. Attach `wrapRef` to the element that
 * contains both the trigger and the floating surface; a mousedown inside it
 * is ignored, anything else closes the popover.
 */
export function usePopover(): {
  open: boolean;
  setOpen: React.Dispatch<React.SetStateAction<boolean>>;
  wrapRef: React.RefObject<HTMLDivElement | null>;
} {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  const close = useCallback(() => setOpen(false), []);
  useDismissOnOutsideClick(wrapRef, close, { enabled: open });

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open]);

  return { open, setOpen, wrapRef };
}
