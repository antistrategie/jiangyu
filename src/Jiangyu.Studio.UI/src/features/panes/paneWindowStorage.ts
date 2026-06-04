import { PANE_KINDS, type PaneKind } from "@features/panes/layout";
import type { AssetBrowserState, TemplateBrowserState } from "@features/panes/browserState";
import { loadJson, removeKey, saveJson } from "@shared/storage";

// Persistent descriptor of a pane window open against a project. The runtime
// windowId that InfiniFrame assigns isn't stored because it's process-local;
// the primary maintains an in-memory map from windowId → descriptor slot so
// it can drop entries when a secondary closes mid-session.
export interface PaneWindowDescriptor {
  readonly kind: PaneKind;
  readonly filePaths: readonly string[];
  readonly activeFilePath: string | null;
  // Populated for browser windows. Shape depends on `kind`.
  readonly browserState?: AssetBrowserState | TemplateBrowserState | undefined;
}

const KEY_PREFIX = "jiangyu.panewindows.";

function keyFor(projectPath: string): string {
  return KEY_PREFIX + projectPath;
}

function isArray(value: unknown): value is unknown[] {
  return Array.isArray(value);
}

export function loadPaneWindows(projectPath: string): PaneWindowDescriptor[] {
  // Permissive filter: drop entries that don't pass isDescriptor, keep the
  // rest. A partial descriptor list from an older schema shouldn't blow away
  // the user's open windows.
  const parsed = loadJson(keyFor(projectPath), isArray);
  if (parsed === null) return [];
  return parsed.filter(isDescriptor);
}

export function savePaneWindows(
  projectPath: string,
  descriptors: readonly PaneWindowDescriptor[],
): void {
  if (descriptors.length === 0) removeKey(keyFor(projectPath));
  else saveJson(keyFor(projectPath), descriptors);
}

function isDescriptor(value: unknown): value is PaneWindowDescriptor {
  if (typeof value !== "object" || value === null) return false;
  const d = value as Partial<PaneWindowDescriptor>;
  if (!(PANE_KINDS as readonly string[]).includes(d.kind ?? "")) return false;
  if (!Array.isArray(d.filePaths) || !d.filePaths.every((p) => typeof p === "string")) return false;
  if (d.activeFilePath !== null && typeof d.activeFilePath !== "string") return false;
  // browserState is opaque — trust the shape if present (the browsers fall
  // back to defaults for missing fields).
  return true;
}
