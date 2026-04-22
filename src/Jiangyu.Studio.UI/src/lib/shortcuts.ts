/**
 * Keyboard-shortcut matcher used by the app-level dispatch table. Browsers
 * report `e.key` in layout-aware form ("p", "\\", "|", "Escape"), which is
 * what callers should pass as `key`. Letter matches are case-insensitive so
 * `key: "p"` matches both "p" and "P" (for when Shift is held).
 */
export interface KeyBinding {
  readonly mod?: boolean;
  readonly shift?: boolean;
  readonly alt?: boolean;
  readonly key: string;
}

export function matchBinding(e: KeyboardEvent, binding: KeyBinding): boolean {
  const mod = e.ctrlKey || e.metaKey;
  if ((binding.mod ?? false) !== mod) return false;
  if ((binding.shift ?? false) !== e.shiftKey) return false;
  if ((binding.alt ?? false) !== e.altKey) return false;
  return binding.key.toLowerCase() === e.key.toLowerCase();
}
