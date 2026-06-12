/**
 * Studio UI user preferences. The source of truth is the host filesystem
 * (persisted via RPC to `studio.json` alongside `config.json` in the
 * Jiangyu config directory). localStorage is a fast mirror — read
 * synchronously on startup for instant paint, then reconciled against
 * the authoritative values fetched via RPC.
 *
 * Each setting is declared once through `defineSetting`, which carries the
 * clamp/validate logic next to the setting, self-registers into the
 * reconciliation sweep, and produces the load/save/hook trio. A single
 * malformed entry falls back to the default instead of wiping the whole
 * blob.
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

// --- setting factory ---------------------------------------------------------

/** Keys are constrained to StudioSettings so a setting can't exist in the
 *  UI without a host-side field, and vice versa a host rename surfaces as
 *  a compile error here. The same string names the localStorage entry
 *  (prefixed) and the `setStudioSetting` RPC key. */
type SettingKey = keyof StudioSettings;

interface SettingHandle<T> {
  readonly load: () => T;
  readonly save: (value: T) => void;
  readonly use: () => [T, (value: T) => void];
}

/** Every defined setting; syncToLocalStorage sweeps this so adding a
 *  setting cannot miss the reconciliation list. */
const registry: { readonly key: SettingKey; readonly storageKey: string }[] = [];

function defineSetting<T extends string | number | boolean>(
  key: SettingKey,
  options: { readonly default: T; readonly parse: (raw: unknown) => T },
): SettingHandle<T> {
  const storageKey = `${STORAGE_PREFIX}${key}`;
  registry.push({ key, storageKey });

  const load = (): T => options.parse(loadRaw(storageKey));

  const save = (value: T): void => {
    const next = options.parse(value);
    saveJson(storageKey, next);
    void persistSetting(key, next);
    notify();
  };

  function useSetting(): [T, (value: T) => void] {
    const value = useSyncExternalStore(subscribe, load, () => options.default);
    const set = useCallback((next: T) => save(next), []);
    return [value, set];
  }

  return { load, save, use: useSetting };
}

function parseBool(fallback: boolean): (raw: unknown) => boolean {
  return (raw) => (typeof raw === "boolean" ? raw : fallback);
}

function clampInt(min: number, max: number, fallback: number): (raw: unknown) => number {
  return (raw) => {
    const value = typeof raw === "number" && Number.isFinite(raw) ? Math.round(raw) : fallback;
    return Math.max(min, Math.min(max, value));
  };
}

function parseEnum<T extends string>(values: readonly T[], fallback: T): (raw: unknown) => T {
  return (raw) => ((values as readonly unknown[]).includes(raw) ? (raw as T) : fallback);
}

// --- editor font size ------------------------------------------------------

export const EDITOR_FONT_SIZE_MIN = 8;
export const EDITOR_FONT_SIZE_MAX = 32;
export const EDITOR_FONT_SIZE_DEFAULT = 14;

const editorFontSize = defineSetting("editorFontSize", {
  default: EDITOR_FONT_SIZE_DEFAULT,
  parse: clampInt(EDITOR_FONT_SIZE_MIN, EDITOR_FONT_SIZE_MAX, EDITOR_FONT_SIZE_DEFAULT),
});

export const loadEditorFontSize = editorFontSize.load;
export const saveEditorFontSize = editorFontSize.save;
export const useEditorFontSize = editorFontSize.use;

// --- UI font scale ---------------------------------------------------------

export const UI_FONT_SCALE_MIN = 80;
export const UI_FONT_SCALE_MAX = 130;
export const UI_FONT_SCALE_DEFAULT = 100;

const uiFontScale = defineSetting("uiFontScale", {
  default: UI_FONT_SCALE_DEFAULT,
  parse: clampInt(UI_FONT_SCALE_MIN, UI_FONT_SCALE_MAX, UI_FONT_SCALE_DEFAULT),
});

export const loadUiFontScale = uiFontScale.load;
export const saveUiFontScale = uiFontScale.save;
export const useUiFontScale = uiFontScale.use;

/** Side-effect hook: mirror the user's UI font scale into the `--fs-scale`
 *  custom property on the document root. Call once at each window's root. */
export function useApplyUiFontScale(): void {
  const [scale] = useUiFontScale();
  useEffect(() => {
    document.documentElement.style.setProperty("--fs-scale", String(scale / 100));
  }, [scale]);
}

// --- editor word wrap ------------------------------------------------------

export type EditorWordWrap = "on" | "off";
export const EDITOR_WORD_WRAP_DEFAULT: EditorWordWrap = "on";

const editorWordWrap = defineSetting<EditorWordWrap>("editorWordWrap", {
  default: EDITOR_WORD_WRAP_DEFAULT,
  parse: parseEnum(["on", "off"], EDITOR_WORD_WRAP_DEFAULT),
});

export const loadEditorWordWrap = editorWordWrap.load;
export const saveEditorWordWrap = editorWordWrap.save;
export const useEditorWordWrap = editorWordWrap.use;

// --- sidebar hidden --------------------------------------------------------

export const SIDEBAR_HIDDEN_DEFAULT = false;

const sidebarHidden = defineSetting("sidebarHidden", {
  default: SIDEBAR_HIDDEN_DEFAULT,
  parse: parseBool(SIDEBAR_HIDDEN_DEFAULT),
});

export const loadSidebarHidden = sidebarHidden.load;
export const saveSidebarHidden = sidebarHidden.save;
export const useSidebarHidden = sidebarHidden.use;

// --- session restore -------------------------------------------------------

export const SESSION_RESTORE_PROJECT_DEFAULT = true;
export const SESSION_RESTORE_TABS_DEFAULT = true;

const sessionRestoreProject = defineSetting("sessionRestoreProject", {
  default: SESSION_RESTORE_PROJECT_DEFAULT,
  parse: parseBool(SESSION_RESTORE_PROJECT_DEFAULT),
});

export const loadSessionRestoreProject = sessionRestoreProject.load;
export const saveSessionRestoreProject = sessionRestoreProject.save;
export const useSessionRestoreProject = sessionRestoreProject.use;

const sessionRestoreTabs = defineSetting("sessionRestoreTabs", {
  default: SESSION_RESTORE_TABS_DEFAULT,
  parse: parseBool(SESSION_RESTORE_TABS_DEFAULT),
});

export const loadSessionRestoreTabs = sessionRestoreTabs.load;
export const saveSessionRestoreTabs = sessionRestoreTabs.save;
export const useSessionRestoreTabs = sessionRestoreTabs.use;

// --- editor keybind mode ---------------------------------------------------

export type EditorKeybindMode = "default" | "vim";
export const EDITOR_KEYBIND_MODE_DEFAULT: EditorKeybindMode = "default";

const editorKeybindMode = defineSetting<EditorKeybindMode>("editorKeybindMode", {
  default: EDITOR_KEYBIND_MODE_DEFAULT,
  parse: parseEnum(["default", "vim"], EDITOR_KEYBIND_MODE_DEFAULT),
});

export const loadEditorKeybindMode = editorKeybindMode.load;
export const saveEditorKeybindMode = editorKeybindMode.save;
export const useEditorKeybindMode = editorKeybindMode.use;

// --- template editor default mode -------------------------------------------

export type TemplateEditorMode = "visual" | "source";
export const TEMPLATE_EDITOR_MODE_DEFAULT: TemplateEditorMode = "visual";

const templateEditorMode = defineSetting<TemplateEditorMode>("templateEditorMode", {
  default: TEMPLATE_EDITOR_MODE_DEFAULT,
  parse: parseEnum(["visual", "source"], TEMPLATE_EDITOR_MODE_DEFAULT),
});

export const loadTemplateEditorMode = templateEditorMode.load;
export const saveTemplateEditorMode = templateEditorMode.save;
export const useTemplateEditorMode = templateEditorMode.use;

// --- AI enabled ------------------------------------------------------------

export const AI_ENABLED_DEFAULT = false;

const aiEnabled = defineSetting("aiEnabled", {
  default: AI_ENABLED_DEFAULT,
  parse: parseBool(AI_ENABLED_DEFAULT),
});

export const loadAiEnabled = aiEnabled.load;
export const saveAiEnabled = aiEnabled.save;
export const useAiEnabled = aiEnabled.use;

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
  for (const { key, storageKey } of registry) {
    saveJson(storageKey, settings[key]);
  }
}

/**
 * Persist a single setting to the host filesystem. Fire-and-forget —
 * the synchronous localStorage write in the save function handles the
 * in-session mirror; this call backfills the durable store.
 *
 * Rapid successive saves on different keys race (each host-side
 * read-modify-write loads the file, applies one change, and writes back
 * independently). The last writer wins; the earlier change lands in
 * localStorage but may be absent from the file until the next save
 * triggers a fresh round-trip. For user preferences this is acceptable.
 */
async function persistSetting(key: SettingKey, value: number | boolean | string): Promise<void> {
  try {
    const settings = await rpcCall<StudioSettings>("setStudioSetting", { key, value });
    // Reconcile: the host returned the full settings object. Write it back
    // so we pick up any clamping the host applied, and so other settings
    // changed by a concurrent window are reflected.
    syncToLocalStorage(settings);
  } catch {
    // Host not available — the localStorage write in the save function
    // is sufficient for this session.
  }
}
