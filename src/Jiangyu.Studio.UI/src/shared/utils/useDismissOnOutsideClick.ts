import { useEffect, useRef, type RefObject } from "react";

interface DismissOptions {
  /** Gate for callers that keep the surface mounted and track an open flag. */
  readonly enabled?: boolean;
  /**
   * Listen in the capture phase (default) so a click swallowed by
   * `stopPropagation` elsewhere still dismisses the surface.
   */
  readonly capture?: boolean;
  /** Also dismiss on any scroll (anchored surfaces drift otherwise). */
  readonly dismissOnScroll?: boolean;
  /** Also dismiss when the window loses focus. */
  readonly dismissOnBlur?: boolean;
}

type ElementRef = RefObject<HTMLElement | null>;

/**
 * Dismiss a floating surface (menu, popover, combobox dropdown) when the
 * user mousedowns outside it. Pass every element that counts as "inside"
 * (the surface plus its anchor/trigger); a mousedown contained by any of
 * them is ignored.
 */
export function useDismissOnOutsideClick(
  refs: ElementRef | readonly ElementRef[],
  onDismiss: () => void,
  options?: DismissOptions,
): void {
  const enabled = options?.enabled ?? true;
  const capture = options?.capture ?? true;
  const dismissOnScroll = options?.dismissOnScroll ?? false;
  const dismissOnBlur = options?.dismissOnBlur ?? false;

  // Latest-value refs so the document listeners never need re-attaching
  // when the callback or ref list identity changes.
  const onDismissRef = useRef(onDismiss);
  const refsRef = useRef(refs);
  useEffect(() => {
    onDismissRef.current = onDismiss;
    refsRef.current = refs;
  });

  useEffect(() => {
    if (!enabled) return;
    const dismiss = () => {
      onDismissRef.current();
    };
    const onMouseDown = (e: MouseEvent) => {
      if (!(e.target instanceof Node)) return;
      const current = refsRef.current;
      const list: readonly ElementRef[] = Array.isArray(current)
        ? (current as readonly ElementRef[])
        : [current as ElementRef];
      for (const ref of list) {
        if (ref.current?.contains(e.target)) return;
      }
      dismiss();
    };
    document.addEventListener("mousedown", onMouseDown, capture);
    if (dismissOnScroll) document.addEventListener("scroll", dismiss, true);
    if (dismissOnBlur) window.addEventListener("blur", dismiss);
    return () => {
      document.removeEventListener("mousedown", onMouseDown, capture);
      if (dismissOnScroll) document.removeEventListener("scroll", dismiss, true);
      if (dismissOnBlur) window.removeEventListener("blur", dismiss);
    };
  }, [enabled, capture, dismissOnScroll, dismissOnBlur]);
}
