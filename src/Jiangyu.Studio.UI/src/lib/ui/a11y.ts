import type { KeyboardEvent } from "react";

/**
 * Standard keyboard activator for non-button elements that have an `onClick`.
 * Pair with `role="button"` and `tabIndex={0}` so the element shows up in the
 * tab order and announces correctly to screen readers.
 *
 * Only Enter and Space activate, matching native button behaviour. Modifier
 * keys are ignored so Ctrl+Click composability is preserved upstream.
 */
export function onKeyActivate<E extends Element>(
  fn: (event: KeyboardEvent<E>) => void,
): (event: KeyboardEvent<E>) => void {
  return (event) => {
    if (event.key !== "Enter" && event.key !== " ") return;
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    fn(event);
  };
}
