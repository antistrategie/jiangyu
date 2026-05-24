/**
 * Studio UI user preferences. The source of truth is the host filesystem
 * (persisted via RPC to `studio.json` alongside `config.json` in the
 * Jiangyu config directory). localStorage is a fast mirror — read
 * synchronously on startup for instant paint, then reconciled against
 * the authoritative values fetched via RPC.
 *
 * Each setting has its own load/save pair so that clamp/validate logic
 * lives next to the setting; a single malformed entry falls back to the
 * default instead of wiping the whole blob.
 *
 * Hook updates are broadcast via a local pub-sub so multiple editor panes
 * reading the same setting stay in lock-step within one window.
 */
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { rpcCall, type StudioSettings } from "@shared/rpc";
import { loadRaw, saveJson } from "@shared/storage";

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

function parseBool(raw: unknown, fallback: boolean): boolean {
  return typeof raw === "boolean" ? raw : fallback;
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
  return clampEditorFontSize(loadRaw(EDITOR_FONT_SIZE_KEY));
}

export function saveEditorFontSize(value: number): void {
  const clamped = clampEditorFontSize(value);
  saveJson(EDITOR_FONT_SIZE_KEY, clamped);
  void persistSetting("editorFontSize", clamped);
  notify();
}

// --- UI font scale ---------------------------------------------------------

export const UI_FONT_SCALE_MIN = 80;
export const UI_FONT_SCALE_MAX = 130;
export const UI_FONT_SCALE_DEFAULT = 100;
const UI_FONT_SCALE_KEY = `${STORAGE_PREFIX}uiFontScale`;

function clampUiFontScale(n: unknown): number {
  const value = typeof n === "number" && Number.isFinite(n) ? Math.round(n) : UI_FONT_SCALE_DEFAULT;
  return Math.max(UI_FONT_SCALE_MIN, Math.min(UI_FONT_SCALE_MAX, value));
}

export function loadUiFontScale(): number {
  return clampUiFontScale(loadRaw(UI_FONT_SCALE_KEY));
}

export function saveUiFontScale(value: number): void {
  const clamped = clampUiFontScale(value);
  saveJson(UI_FONT_SCALE_KEY, clamped);
  void persistSetting("uiFontScale", clamped);
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
  return parseEditorWordWrap(loadRaw(EDITOR_WORD_WRAP_KEY));
}

export function saveEditorWordWrap(value: EditorWordWrap): void {
  saveJson(EDITOR_WORD_WRAP_KEY, value);
  void persistSetting("editorWordWrap", value);
  notify();
}

// --- sidebar hidden --------------------------------------------------------

export const SIDEBAR_HIDDEN_DEFAULT = false;
const SIDEBAR_HIDDEN_KEY = `${STORAGE_PREFIX}sidebarHidden`;

export function loadSidebarHidden(): boolean {
  return parseBool(loadRaw(SIDEBAR_HIDDEN_KEY), SIDEBAR_HIDDEN_DEFAULT);
}

export function saveSidebarHidden(value: boolean): void {
  saveJson(SIDEBAR_HIDDEN_KEY, value);
  void persistSetting("sidebarHidden", value);
  notify();
}

// --- session restore -------------------------------------------------------

export const SESSION_RESTORE_PROJECT_DEFAULT = true;
export const SESSION_RESTORE_TABS_DEFAULT = true;
const SESSION_RESTORE_PROJECT_KEY = `${STORAGE_PREFIX}sessionRestoreProject`;
const SESSION_RESTORE_TABS_KEY = `${STORAGE_PREFIX}sessionRestoreTabs`;

export function loadSessionRestoreProject(): boolean {
  return parseBool(loadRaw(SESSION_RESTORE_PROJECT_KEY), SESSION_RESTORE_PROJECT_DEFAULT);
}

export function saveSessionRestoreProject(value: boolean): void {
  saveJson(SESSION_RESTORE_PROJECT_KEY, value);
  void persistSetting("sessionRestoreProject", value);
  notify();
}

export function loadSessionRestoreTabs(): boolean {
  return parseBool(loadRaw(SESSION_RESTORE_TABS_KEY), SESSION_RESTORE_TABS_DEFAULT);
}

export function saveSessionRestoreTabs(value: boolean): void {
  saveJson(SESSION_RESTORE_TABS_KEY, value);
  void persistSetting("sessionRestoreTabs", value);
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
  return parseEditorKeybindMode(loadRaw(EDITOR_KEYBIND_MODE_KEY));
}

export function saveEditorKeybindMode(value: EditorKeybindMode): void {
  saveJson(EDITOR_KEYBIND_MODE_KEY, value);
  void persistSetting("editorKeybindMode", value);
  notify();
}

// --- template editor default mode -------------------------------------------

export type TemplateEditorMode = "visual" | "source";
export const TEMPLATE_EDITOR_MODE_DEFAULT: TemplateEditorMode = "visual";
const TEMPLATE_EDITOR_MODE_KEY = `${STORAGE_PREFIX}templateEditorMode`;

function parseTemplateEditorMode(raw: unknown): TemplateEditorMode {
  return raw === "source" ? "source" : TEMPLATE_EDITOR_MODE_DEFAULT;
}

export function loadTemplateEditorMode(): TemplateEditorMode {
  return parseTemplateEditorMode(loadRaw(TEMPLATE_EDITOR_MODE_KEY));
}

export function saveTemplateEditorMode(value: TemplateEditorMode): void {
  saveJson(TEMPLATE_EDITOR_MODE_KEY, value);
  void persistSetting("templateEditorMode", value);
  notify();
}

// --- AI enabled ------------------------------------------------------------

export const AI_ENABLED_DEFAULT = false;
const AI_ENABLED_KEY = `${STORAGE_PREFIX}aiEnabled`;

export function loadAiEnabled(): boolean {
  return parseBool(loadRaw(AI_ENABLED_KEY), AI_ENABLED_DEFAULT);
}

export function saveAiEnabled(value: boolean): void {
  saveJson(AI_ENABLED_KEY, value);
  void persistSetting("aiEnabled", value);
  notify();
}

// --- RPC persistence -------------------------------------------------------

/**
 * Call once at app startup (after initRpc) to reconcile the localStorage
 * mirror against the authoritative filesystem values. Runs async and
 * does not block first paint — components read localStorage instantly.
 */
export function initSettings(): void {
  rpcCall<StudioSettings>("getStudioSettings")
    .then((settings) => {
      // Write the authoritative values into localStorage so subsequent
      // synchronous reads (including next launch) see the filesystem state.
      syncToLocalStorage(settings);
      notify();
    })
    .catch(() => {
      // Host not available (e.g. running in a browser during dev).
      // localStorage values are the best we have.
    });
}

function syncToLocalStorage(settings: StudioSettings): void {
  saveJson(EDITOR_FONT_SIZE_KEY, settings.editorFontSize);
  saveJson(UI_FONT_SCALE_KEY, settings.uiFontScale);
  saveJson(EDITOR_WORD_WRAP_KEY, settings.editorWordWrap);
  saveJson(SIDEBAR_HIDDEN_KEY, settings.sidebarHidden);
  saveJson(SESSION_RESTORE_PROJECT_KEY, settings.sessionRestoreProject);
  saveJson(SESSION_RESTORE_TABS_KEY, settings.sessionRestoreTabs);
  saveJson(EDITOR_KEYBIND_MODE_KEY, settings.editorKeybindMode);
  saveJson(TEMPLATE_EDITOR_MODE_KEY, settings.templateEditorMode);
  saveJson(AI_ENABLED_KEY, settings.aiEnabled);
}

/**
 * Persist a single setting to the host filesystem. Fire-and-forget —
 * the synchronous localStorage write in the save* function handles the
 * in-session mirror; this call backfills the durable store.
 *
 * Rapid successive saves on different keys race (each host-side
 * read-modify-write loads the file, applies one change, and writes back
 * independently). The last writer wins; the earlier change lands in
 * localStorage but may be absent from the file until the next save
 * triggers a fresh round-trip. For user preferences this is acceptable.
 */
async function persistSetting(key: string, value: number | boolean | string): Promise<void> {
  try {
    const settings = await rpcCall<StudioSettings>("setStudioSetting", { key, value });
    // Reconcile: the host returned the full settings object. Write it back
    // so we pick up any clamping the host applied, and so other settings
    // changed by a concurrent window are reflected.
    syncToLocalStorage(settings);
  } catch {
    // Host not available — the localStorage write in the save* function
    // is sufficient for this session.
  }
}

// --- hooks -----------------------------------------------------------------

export function useEditorFontSize(): [number, (value: number) => void] {
  const value = useSyncExternalStore(subscribe, loadEditorFontSize, () => EDITOR_FONT_SIZE_DEFAULT);
  const set = useCallback((next: number) => saveEditorFontSize(next), []);
  return [value, set];
}

export function useUiFontScale(): [number, (value: number) => void] {
  const value = useSyncExternalStore(subscribe, loadUiFontScale, () => UI_FONT_SCALE_DEFAULT);
  const set = useCallback((next: number) => saveUiFontScale(next), []);
  return [value, set];
}

export function useSidebarHidden(): [boolean, (value: boolean) => void] {
  const value = useSyncExternalStore(subscribe, loadSidebarHidden, () => SIDEBAR_HIDDEN_DEFAULT);
  const set = useCallback((next: boolean) => saveSidebarHidden(next), []);
  return [value, set];
}

export function useSessionRestoreProject(): [boolean, (value: boolean) => void] {
  const value = useSyncExternalStore(
    subscribe,
    loadSessionRestoreProject,
    () => SESSION_RESTORE_PROJECT_DEFAULT,
  );
  const set = useCallback((next: boolean) => saveSessionRestoreProject(next), []);
  return [value, set];
}

export function useSessionRestoreTabs(): [boolean, (value: boolean) => void] {
  const value = useSyncExternalStore(
    subscribe,
    loadSessionRestoreTabs,
    () => SESSION_RESTORE_TABS_DEFAULT,
  );
  const set = useCallback((next: boolean) => saveSessionRestoreTabs(next), []);
  return [value, set];
}

/** Side-effect hook: mirror the user's UI font scale into the `--fs-scale`
 *  custom property on the document root. Call once at each window's root. */
export function useApplyUiFontScale(): void {
  const [scale] = useUiFontScale();
  useEffect(() => {
    document.documentElement.style.setProperty("--fs-scale", String(scale / 100));
  }, [scale]);
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

export function useTemplateEditorMode(): [TemplateEditorMode, (value: TemplateEditorMode) => void] {
  const value = useSyncExternalStore(
    subscribe,
    loadTemplateEditorMode,
    () => TEMPLATE_EDITOR_MODE_DEFAULT,
  );
  const set = useCallback((next: TemplateEditorMode) => saveTemplateEditorMode(next), []);
  return [value, set];
}

export function useAiEnabled(): [boolean, (value: boolean) => void] {
  const value = useSyncExternalStore(subscribe, loadAiEnabled, () => AI_ENABLED_DEFAULT);
  const set = useCallback((next: boolean) => saveAiEnabled(next), []);
  return [value, set];
}
