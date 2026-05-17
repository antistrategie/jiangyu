import { useCallback, useLayoutEffect, useState, type RefObject } from "react";

export interface AnchorPosition {
  readonly top: number;
  readonly left: number;
  readonly width: number;
}

/**
 * Track the viewport-relative position of an anchor element while `open` is
 * true. Returns `null` when closed, or `{ top: rect.bottom, left: rect.left,
 * width: rect.width }` when open. Updates on scroll (any ancestor, via
 * capture-phase listener) and window resize, so a portalled menu can stay
 * glued to its anchor as the user scrolls the visual editor.
 */
export function useAnchorPosition(
  anchorRef: RefObject<HTMLElement | null>,
  open: boolean,
): AnchorPosition | null {
  const [position, setPosition] = useState<AnchorPosition | null>(null);

  const update = useCallback(() => {
    const el = anchorRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
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
        prev.top === rect.bottom &&
        prev.left === rect.left &&
        prev.width === rect.width
      ) {
        return prev;
      }
      return { top: rect.bottom, left: rect.left, width: rect.width };
    });
  }, [anchorRef]);

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
