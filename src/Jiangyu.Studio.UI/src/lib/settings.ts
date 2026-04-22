/**
 * User preferences persisted to localStorage. UI-only — machine-level paths
 * (game, Unity) stay in `GlobalConfig.json` on the host since the CLI and
 * compiler read them too.
 *
 * Each setting has its own load/save pair so that clamp/validate logic lives
 * next to the setting; a single malformed entry falls back to the default
 * instead of wiping the whole blob.
 *
 * Hook updates are broadcast via a local pub-sub so multiple editor panes
 * reading the same setting stay in lock-step within one window. The `storage`
 * event covers the rare cross-window case.
 */
import { useCallback, useSyncExternalStore } from "react";

const STORAGE_PREFIX = "jiangyu:setting:";

const subscribers = new Set<() => void>();

function subscribe(fn: () => void): () => void {
  subscribers.add(fn);
  return () => {
    subscribers.delete(fn);
  };
}

function notify(): void {
  for (const fn of subscribers) fn();
}

if (typeof window !== "undefined") {
  window.addEventListener("storage", (e) => {
    if (e.key !== null && e.key.startsWith(STORAGE_PREFIX)) notify();
  });
}

// --- editor font size ------------------------------------------------------

export const EDITOR_FONT_SIZE_MIN = 8;
export const EDITOR_FONT_SIZE_MAX = 32;
export const EDITOR_FONT_SIZE_DEFAULT = 14;
const EDITOR_FONT_SIZE_KEY = `${STORAGE_PREFIX}editorFontSize`;

function clampEditorFontSize(n: unknown): number {
  const value =
    typeof n === "number" && Number.isFinite(n) ? Math.round(n) : EDITOR_FONT_SIZE_DEFAULT;
  return Math.max(EDITOR_FONT_SIZE_MIN, Math.min(EDITOR_FONT_SIZE_MAX, value));
}

export function loadEditorFontSize(): number {
  try {
    const raw = localStorage.getItem(EDITOR_FONT_SIZE_KEY);
    if (raw === null) return EDITOR_FONT_SIZE_DEFAULT;
    return clampEditorFontSize(JSON.parse(raw));
  } catch {
    return EDITOR_FONT_SIZE_DEFAULT;
  }
}

export function saveEditorFontSize(value: number): void {
  const clamped = clampEditorFontSize(value);
  try {
    localStorage.setItem(EDITOR_FONT_SIZE_KEY, JSON.stringify(clamped));
  } catch {
    // quota exceeded / storage unavailable — the setting won't persist but
    // the in-memory subscribers still see the change this session.
  }
  notify();
}

// --- editor word wrap ------------------------------------------------------

export type EditorWordWrap = "on" | "off";
export const EDITOR_WORD_WRAP_DEFAULT: EditorWordWrap = "on";
const EDITOR_WORD_WRAP_KEY = `${STORAGE_PREFIX}editorWordWrap`;

function parseEditorWordWrap(raw: unknown): EditorWordWrap {
  return raw === "off" ? "off" : EDITOR_WORD_WRAP_DEFAULT;
}

export function loadEditorWordWrap(): EditorWordWrap {
  try {
    const raw = localStorage.getItem(EDITOR_WORD_WRAP_KEY);
    if (raw === null) return EDITOR_WORD_WRAP_DEFAULT;
    return parseEditorWordWrap(JSON.parse(raw));
  } catch {
    return EDITOR_WORD_WRAP_DEFAULT;
  }
}

export function saveEditorWordWrap(value: EditorWordWrap): void {
  try {
    localStorage.setItem(EDITOR_WORD_WRAP_KEY, JSON.stringify(value));
  } catch {
    // see note on saveEditorFontSize.
  }
  notify();
}

// --- editor keybind mode ---------------------------------------------------

export type EditorKeybindMode = "default" | "vim";
export const EDITOR_KEYBIND_MODE_DEFAULT: EditorKeybindMode = "default";
const EDITOR_KEYBIND_MODE_KEY = `${STORAGE_PREFIX}editorKeybindMode`;

function parseEditorKeybindMode(raw: unknown): EditorKeybindMode {
  return raw === "vim" ? "vim" : EDITOR_KEYBIND_MODE_DEFAULT;
}

export function loadEditorKeybindMode(): EditorKeybindMode {
  try {
    const raw = localStorage.getItem(EDITOR_KEYBIND_MODE_KEY);
    if (raw === null) return EDITOR_KEYBIND_MODE_DEFAULT;
    return parseEditorKeybindMode(JSON.parse(raw));
  } catch {
    return EDITOR_KEYBIND_MODE_DEFAULT;
  }
}

export function saveEditorKeybindMode(value: EditorKeybindMode): void {
  try {
    localStorage.setItem(EDITOR_KEYBIND_MODE_KEY, JSON.stringify(value));
  } catch {
    // see note on saveEditorFontSize.
  }
  notify();
}

// --- hooks -----------------------------------------------------------------

export function useEditorFontSize(): [number, (value: number) => void] {
  const value = useSyncExternalStore(subscribe, loadEditorFontSize, () => EDITOR_FONT_SIZE_DEFAULT);
  const set = useCallback((next: number) => saveEditorFontSize(next), []);
  return [value, set];
}

export function useEditorWordWrap(): [EditorWordWrap, (value: EditorWordWrap) => void] {
  const value = useSyncExternalStore(subscribe, loadEditorWordWrap, () => EDITOR_WORD_WRAP_DEFAULT);
  const set = useCallback((next: EditorWordWrap) => saveEditorWordWrap(next), []);
  return [value, set];
}

export function useEditorKeybindMode(): [EditorKeybindMode, (value: EditorKeybindMode) => void] {
  const value = useSyncExternalStore(
    subscribe,
    loadEditorKeybindMode,
    () => EDITOR_KEYBIND_MODE_DEFAULT,
  );
  const set = useCallback((next: EditorKeybindMode) => saveEditorKeybindMode(next), []);
  return [value, set];
}
