import { useCallback, useLayoutEffect, useState, type RefObject } from "react";

export interface AnchorPosition {
  readonly top: number | "auto";
  readonly bottom: number | "auto";
  readonly left: number;
  readonly width: number;
  readonly maxHeight: number;
}

/**
 * Position a portalled menu beside its anchor within the viewport. Opens
 * above when there is too little room below and limits the menu's height to
 * the available space. Tracks ancestor scrolling and window resizing.
 */
export function useAnchorPosition(
  anchorRef: RefObject<HTMLElement | null>,
  open: boolean,
  { maxHeight = 240, minWidth = 0 }: { maxHeight?: number; minWidth?: number } = {},
): AnchorPosition | null {
  const [position, setPosition] = useState<AnchorPosition | null>(null);

  const update = useCallback(() => {
    const el = anchorRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const margin = 8;
    const viewportBottom = Math.max(margin, window.innerHeight - margin);
    const anchorTop = Math.min(Math.max(rect.top, margin), viewportBottom);
    const anchorBottom = Math.min(Math.max(rect.bottom, margin), viewportBottom);
    const above = anchorTop - margin;
    const below = viewportBottom - anchorBottom;
    const openAbove = below < maxHeight && above > below;
    const width = Math.min(
      Math.max(rect.width, minWidth),
      Math.max(0, window.innerWidth - 2 * margin),
    );
    const next: AnchorPosition = {
      top: openAbove ? "auto" : anchorBottom,
      bottom: openAbove ? window.innerHeight - anchorTop : "auto",
      left: Math.max(margin, Math.min(rect.left, window.innerWidth - margin - width)),
      width,
      maxHeight: Math.min(maxHeight, openAbove ? above : below),
    };
    // Bail when nothing changed. The capture-phase scroll listener fires for
    // every scroll in the document, including unrelated panes; without this
    // check React re-renders the consumer on every one of those, repainting
    // the anchor's siblings (ref labels, op chips) for no visible reason.
    // Measuring the anchor IS the external system this hook synchronises with;
    // setState in the effect is intentional. The lint rule's general advice
    // about cascading renders doesn't apply to a one-shot DOM read.
    // eslint-disable-next-line @eslint-react/set-state-in-effect
    setPosition((prev) => {
      if (
        prev !== null &&
        prev.top === next.top &&
        prev.bottom === next.bottom &&
        prev.left === next.left &&
        prev.width === next.width &&
        prev.maxHeight === next.maxHeight
      ) {
        return prev;
      }
      return next;
    });
  }, [anchorRef, maxHeight, minWidth]);

  useLayoutEffect(() => {
    if (!open) return;
    update();
    window.addEventListener("scroll", update, true);
    window.addEventListener("resize", update);
    return () => {
      window.removeEventListener("scroll", update, true);
      window.removeEventListener("resize", update);
    };
  }, [open, update]);

  // Returning the stale position while closed would render dropdowns at
  // last-known coordinates briefly when they reopen elsewhere; gate on `open`
  // at the call site instead of mutating state from inside the effect.
  return open ? position : null;
}
